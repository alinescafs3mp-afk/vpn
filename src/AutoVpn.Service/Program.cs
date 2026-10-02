using AutoVpn.Application;
using AutoVpn.Contracts;
using AutoVpn.Infrastructure.Broker;
using AutoVpn.Infrastructure.Persistence;

var root = args.Length > 0
    ? args[0]
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoVPN");
Directory.CreateDirectory(root);
Console.WriteLine("AutoVPN broker");
Console.WriteLine("Каталог данных: " + root);
Console.WriteLine("Сетевые фильтры и TUN на этом запуске не устанавливаются: " + UnavailableNetworkGuard.PlatformReason());

// SQLite secrets use DPAPI current-user and are refused off Windows.
// Linux accepts the pipe only after SO_PEERCRED matches the service uid.
// An unverified peer is rejected. Windows SID and pipe ACL are not implemented.
ICatalogue catalogue = OperatingSystem.IsWindows()
    ? SqliteCatalogue.Open(Path.Combine(root, "catalogue.sqlite"), SecretProtectors.ForProductionHost())
    : new MemoryCatalogue();
using var catalogueLifetime = catalogue as IDisposable;
using var journal = EffectJournal.Open(Path.Combine(root, "effects.sqlite"));
var engine = new BrokerEngine(catalogue, new UnavailableNetworkGuard(), new RefusingCoreController(), journal);
var dispatcher = new IpcDispatcher();
await using var server = LocalIpcServer.Start("autovpn-broker", dispatcher, engine, new CallerIdentity { Sid = "unverified", SessionId = 0, IsRemotePipe = false });
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    stop.Cancel();
};
Console.WriteLine("Канал autovpn-broker слушает локально. Остановка не меняет системную сеть.");
var stopTask = Task.Delay(Timeout.Infinite, stop.Token);
var finished = await Task.WhenAny(server.Completion, stopTask).ConfigureAwait(false);
if (finished == server.Completion && !stop.IsCancellationRequested)
{
    Console.WriteLine("Канал остановился: " + (server.PipeFault ?? "UNKNOWN") + ". Служба не остаётся выглядеть живой.");
    return 2;
}

Console.WriteLine("Брокер остановлен.");
return 0;
