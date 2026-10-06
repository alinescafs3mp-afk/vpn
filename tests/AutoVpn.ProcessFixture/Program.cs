// Finite synthetic owned child. Native node mode uses an explicitly provisioned pinned core.
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AutoVpn.Infrastructure.Core;
using AutoVpn.Infrastructure.Probe;
if (args.Length == 5 && args[0] == "-t" && args[1] == "-f" && args[3] == "-d")
    return await ValidatorFixtureAsync(args[2], args[4]);
if (args.Length != 1) return 2;
if (args[0] is "windows-file-use" or "windows-file-use-timeout")
    return await WindowsFileUseControl.RunAsync(args[0] == "windows-file-use-timeout");
if (args[0] == "node-runtime") return await RuntimeNodeSmoke.RunAsync();
if (args[0] is "pool-cancel-stream" or "pool-cancel-drain" or "pool-cancel-native" or "pool-cancel-delay-control") return PipeCancellationControl.Run(args[0]);
if (args[0] is "pool-legacy" or "pool-available" or "pool-runtime" or "pool-validator") return PoolControl(args[0]);
if (args[0] == "sleep") { await Task.Delay(TimeSpan.FromSeconds(30)); return 0; }
if (args[0] == "exit") return 0;
if (args[0] == "exit259") return 259;
if (args[0] == "mark") { File.WriteAllText("spawned.txt", "synthetic owned fixture"); return 0; }
if (args[0] != "flood") return 2;
var clock = Stopwatch.StartNew();
var buffer = new string('x', 4096);
var rounds = 0;
while (clock.Elapsed < TimeSpan.FromSeconds(30))
{
    await Console.Out.WriteAsync(buffer);
    await Console.Out.FlushAsync();
    await Console.Error.WriteAsync(buffer);
    await Console.Error.FlushAsync();
    if (++rounds == 128) File.WriteAllText("output-ready.txt", "Both output streams produced at least 512 KiB.");
}
return 0;

// A finite, credential-free validator-shaped child. It never opens a network listener.
static async Task<int> ValidatorFixtureAsync(string config, string directory)
{
    try
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetFullPath(directory), Path.GetFullPath(Environment.CurrentDirectory), comparison) ||
            !string.Equals(Path.GetFullPath(config), Path.Combine(Path.GetFullPath(directory), "config.yaml"), comparison) ||
            new FileInfo(config).Length > 4096) return 120;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(config, budget.Token));
        var root = json.RootElement;
        var mode = root.GetProperty("mode").GetString();
        if (root.GetProperty("fixture").GetString() != "owned-validator-v1" ||
            mode is not ("finite-output" or "invalid-utf8") ||
            root.GetProperty("tun").GetProperty("enable").GetBoolean()) return 121;
        var exitCode = root.GetProperty("exitCode").GetInt32();
        var marker = root.GetProperty("marker").GetString();
        var control = root.GetProperty("controlDirectory").GetString();
        if (exitCode is not (0 or 23) || marker is null || marker.Length > 256 ||
            string.IsNullOrEmpty(control) || !Directory.Exists(control)) return 122;
        var ready = JsonSerializer.Serialize(new
        { processId = Environment.ProcessId, configPath = Path.GetFullPath(config), directoryPath = Path.GetFullPath(directory) });
        await File.WriteAllTextAsync(Path.Combine(control, "ready.tmp"), ready, budget.Token);
        File.Move(Path.Combine(control, "ready.tmp"), Path.Combine(control, "ready.json"));
        var release = Path.Combine(control, "release.txt");
        var clock = Stopwatch.StartNew();
        while (!File.Exists(release))
        {
            if (clock.Elapsed >= TimeSpan.FromSeconds(10)) return 123;
            await Task.Delay(10, budget.Token);
        }
        Console.OutputEncoding = new UTF8Encoding(false, true);
        if (mode == "invalid-utf8")
        {
            using var raw = Console.OpenStandardOutput();
            await raw.WriteAsync(new byte[] { 0xff }, budget.Token);
            await raw.FlushAsync(budget.Token);
            await Console.Error.WriteAsync("bounded validator stderr\n".AsMemory(), budget.Token);
            await Console.Error.FlushAsync(budget.Token);
            return 0;
        }
        var stdout = "OUT:" + marker + "🙂\\literal\n" + new string('x', 8192);
        var stderr = "ERR:" + marker + "🙂\\literal\n" + new string('y', 8192);
        for (var i = 0; i < 6; i++)
        {
            await Console.Out.WriteAsync(stdout.AsMemory(), budget.Token);
            await Console.Out.FlushAsync(budget.Token);
            await Console.Error.WriteAsync(stderr.AsMemory(), budget.Token);
            await Console.Error.FlushAsync(budget.Token);
        }
        return exitCode;
    }
    catch (Exception) { return 124; } // No paths, control content, or native exception text.
}

// Runs in an isolated disposable child, never changes the test runner or product pool.
static int PoolControl(string mode)
{
    if (!OperatingSystem.IsWindows()) return 5;
    if (!ThreadPool.SetMinThreads(2, 2) || !ThreadPool.SetMaxThreads(2, 2)) return 6;
    var start = new ProcessStartInfo(Environment.ProcessPath!)
    { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    // Framework-dependent fixture: host is dotnet. Native apphost needs no DLL argument.
    if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
    start.ArgumentList.Add("sleep");
    using var child = Process.Start(start)!;
    using var stop = new CancellationTokenSource();
    using var marker = new ManualResetEventSlim();
    var legacy = mode == "pool-legacy";
    Task Read(StreamReader reader) => mode == "pool-runtime"
        ? MihomoRuntimeProcess.ReadOutputAsync(reader)
        : mode == "pool-validator" ? MihomoProcessController.DrainAsync(reader)
        : legacy ? ProbeOutputDrain.ReadCoreAsync(reader, stop.Token) : ProbeOutputDrain.ReadAsync(reader, stop.Token);
    var stdout = Read(child.StandardOutput); var stderr = Read(child.StandardError);
    var outcome = false;
    try
    {
        if (legacy)
        {
            var clock = Stopwatch.StartNew();
            do
            {
                ThreadPool.GetAvailableThreads(out var workers, out _);
                if (workers == 0) break;
                if (clock.Elapsed > TimeSpan.FromSeconds(3)) return 7;
                Thread.Sleep(5);
            } while (true);
        }
        ThreadPool.QueueUserWorkItem(_ => marker.Set());
        var responsive = marker.Wait(TimeSpan.FromMilliseconds(300));
        outcome = legacy ? !responsive : responsive;
    }
    finally
    {
        child.Kill(true);
        if (!child.WaitForExit(2000)) throw new InvalidOperationException("OWNED_CHILD_STOP_FAILED");
        stop.Cancel();
        if (!Task.WhenAll(stdout, stderr).Wait(TimeSpan.FromSeconds(2)))
            throw new InvalidOperationException("OUTPUT_CONTROL_CLEANUP_FAILED");
        if (!marker.Wait(TimeSpan.FromSeconds(2))) throw new InvalidOperationException("POOL_NOT_RELEASED");
    }
    Console.WriteLine(outcome ? "CONTROL_PASSED:" + mode : "CONTROL_FAILED:" + mode);
    return outcome ? 0 : 1;
}
