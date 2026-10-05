// Finite synthetic owned child. No network or file input. Markers use only its test-owned working directory.
using System.Diagnostics;
using AutoVpn.Infrastructure.Probe;
if (args.Length != 1) return 2;
if (args[0] is "pool-legacy" or "pool-available") return PoolControl(args[0]);
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
    Task<ProbeOutputResult> Read(StreamReader reader) => legacy
        ? ProbeOutputDrain.ReadCoreAsync(reader, stop.Token) : ProbeOutputDrain.ReadAsync(reader, stop.Token);
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
