using AutoVpn.Application;
using AutoVpn.Contracts;
using AutoVpn.Infrastructure.Broker;
using AutoVpn.Infrastructure.Core;
using AutoVpn.Infrastructure.Fetch;
using AutoVpn.Infrastructure.Persistence;
using AutoVpn.Infrastructure.Probe;

// Installed mode never opens the per-user catalogue or instantiates the development runtime.
if (args is ["--service"])
{
    if (!OperatingSystem.IsWindows()) return 5;
    try { return new AutoVpn.Service.NativeServiceHost().Run(); }
    catch (System.ComponentModel.Win32Exception) { Console.Error.WriteLine("SCM_START_REQUIRED"); return 5; }
}
if (args is ["--service-status"])
{
    var result = await AutoVpn.Infrastructure.WindowsService.InstalledServiceClient.QueryAsync();
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, IpcJson.Options));
    return result.State == "Ready" ? 0 : 6;
}
if (args.Any(a => a.StartsWith("--", StringComparison.Ordinal)) || args.Length > 1)
{
    Console.Error.WriteLine("USAGE: AutoVpn.Service [catalogue-directory | --service | --service-status]");
    return 5;
}
if (OperatingSystem.IsWindows())
{
    using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
    if (identity.User?.Value is "S-1-5-18" or "S-1-5-19" or "S-1-5-20")
    {
        Console.Error.WriteLine("DEVELOPMENT_BROKER_REQUIRES_USER_ACCOUNT");
        return 5;
    }
}

var root = args.Length > 0 ? args[0]
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoVPN");
Directory.CreateDirectory(root);
Console.WriteLine("AutoVPN V3: брокер разработки, не установленная Windows-служба.");
Console.WriteLine("TUN и сетевые фильтры не включаются: " + UnavailableNetworkGuard.PlatformReason());

ICatalogue catalogue;
try
{
    // The desktop owns catalogue writes. A long-lived broker refreshes committed
    // data without a second mutable snapshot competing with maintenance writes.
    catalogue = OperatingSystem.IsWindows()
        ? SqliteCatalogue.OpenReadOnly(Path.Combine(root, "catalogue.sqlite"), SecretProtectors.ForProductionHost())
        : new MemoryCatalogue();
}
catch (Exception ex) when (ex is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or
    System.Security.Cryptography.CryptographicException)
{
    Console.WriteLine("Каталог недоступен. Сначала запустите интерфейс под тем же пользователем. Код: " + ex.GetType().Name);
    return 4;
}
using var catalogueLifetime = catalogue as IDisposable;
using var journal = EffectJournal.Open(Path.Combine(root, "effects.sqlite"));
var installation = CoreInstallation.Read(AppContext.BaseDirectory);
var registry = ReviewedRegistryLoader.Load(Path.Combine(AppContext.BaseDirectory, "config"));
// Pure identity validation only. Revalidation stays with the consent-bound,
// budgeted maintenance owner; this host must not create another probe loop.
var targetSetId = TwoTargetProbeTransport.TargetIdentity(registry.ProbeTargets);
await using var core = new OwnedCoreSupervisor(() => new MihomoRuntimeProcess(installation.Path, installation.Sha256));
var engine = new BrokerEngine(catalogue, new UnavailableNetworkGuard(), core, journal,
    requiredTargetSetId: targetSetId);
await using var monitor = new BrokerSafetyMonitor(engine);
var dispatcher = new IpcDispatcher();
await using var server = LocalIpcServer.Start("autovpn-broker", dispatcher, engine,
    new CallerIdentity { Sid = "unverified", SessionId = 0, IsRemotePipe = false });
using var stop = new CancellationTokenSource();
ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; stop.Cancel(); };
Console.CancelKeyPress += cancel;
var exitCode = 0;
try
{
    Console.WriteLine("Локальный канал слушает. Ctrl+C выполняет остановку принадлежащей брокеру сессии.");
    var finished = await Task.WhenAny(server.Completion, monitor.Completion, Task.Delay(Timeout.Infinite, stop.Token)).ConfigureAwait(false);
    if ((finished == server.Completion || finished == monitor.Completion) && !stop.IsCancellationRequested)
    {
        Console.WriteLine(finished == monitor.Completion
                ? "Наблюдение состояния остановилось. Выполняется безопасная остановка."
                : "Канал остановился: " + (server.PipeFault ?? "UNKNOWN"));
        exitCode = 2;
    }
}
finally
{
    Console.CancelKeyPress -= cancel;
    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    try
    {
        if (!await engine.ShutdownAsync(cleanup.Token).ConfigureAwait(false))
        {
            Console.WriteLine("Остановка не подтверждена. Состояние не считается очищенным.");
            exitCode = 3;
        }
        else Console.WriteLine("Остановка своей сессии подтверждена. Проверка системной защиты этим не заменяется.");
    }
    catch (Exception ex) when (ex is OperationCanceledException or IOException)
    {
        Console.WriteLine("Очистка остаётся незавершённой; требуется проверка. Код: " + ex.GetType().Name);
        exitCode = 3;
    }
}
return exitCode;
