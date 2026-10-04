// Finite, synthetic owned child for cross-platform lifetime tests. No network or file input.
using System.Diagnostics;
if (args.Length != 1) return 2;
if (args[0] == "sleep") { await Task.Delay(TimeSpan.FromSeconds(30)); return 0; }
if (args[0] == "exit") return 0;
if (args[0] != "flood") return 2;
var clock = Stopwatch.StartNew();
var buffer = new string('x', 4096);
while (clock.Elapsed < TimeSpan.FromSeconds(30))
{
    await Console.Out.WriteAsync(buffer);
    await Console.Out.FlushAsync();
    await Console.Error.WriteAsync(buffer);
    await Console.Error.FlushAsync();
}
return 0;
