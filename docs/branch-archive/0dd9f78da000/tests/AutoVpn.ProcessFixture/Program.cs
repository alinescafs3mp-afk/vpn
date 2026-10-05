// Finite synthetic owned child. No network or file input. Readiness is written only in its test-owned working directory.
using System.Diagnostics;
if (args.Length != 1) return 2;
if (args[0] == "sleep") { await Task.Delay(TimeSpan.FromSeconds(30)); return 0; }
if (args[0] == "exit") return 0;
if (args[0] == "exit259") return 259;
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
