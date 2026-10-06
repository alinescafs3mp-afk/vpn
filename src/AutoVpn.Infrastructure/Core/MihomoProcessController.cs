using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Probe;

namespace AutoVpn.Infrastructure.Core;

public sealed record CoreValidationResult(bool Ok, string? ReasonCode, string RedactedOutput);

public static class MihomoProcessController
{
    public static async Task<CoreValidationResult> ValidateAsync(string binaryPath, string expectedSha256,
        string yaml, CancellationToken cancellationToken)
    {
        if (MihomoProfileGenerator.EnablesTun(yaml) && !OperatingSystem.IsWindows())
            return new(false, ReasonCodes.NotWindows, "TUN validation is refused on this operating system.");
        if (!File.Exists(binaryPath)) return new(false, "CORE_MISSING", "Pinned core binary is not present.");
        if (cancellationToken.IsCancellationRequested) return new(false, ReasonCodes.Canceled, "Validation canceled before launch.");
        // Keep a read-only, non-delete-sharing handle through execution on Windows.
        await using var binary = new FileStream(binaryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(binary, cancellationToken).ConfigureAwait(false));
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            return new(false, "CORE_HASH", "Pinned core hash does not match.");

        var directory = Directory.CreateTempSubdirectory("autovpn-validate-");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var process = new Process();
        var started = false;
        try
        {
            var config = Path.Combine(directory.FullName, "config.yaml");
            await File.WriteAllTextAsync(config, yaml, cancellationToken).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(config, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            process.StartInfo = new ProcessStartInfo
            {
                FileName = Path.GetFullPath(binaryPath), WorkingDirectory = directory.FullName,
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var argument in new[] { "-t", "-f", config, "-d", directory.FullName }) process.StartInfo.ArgumentList.Add(argument);
            process.StartInfo.Environment["HOME"] = directory.FullName;
            cancellationToken.ThrowIfCancellationRequested();
            started = process.Start();
            if (!started) return new(false, ReasonCodes.CoreConfigRejected, "Core process did not start.");
            // Always drain both pipes. Do NOT retain raw core output: it can include imported credentials.
            var stdout = DrainAsync(process.StandardOutput);
            var stderr = DrainAsync(process.StandardError);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            string? reason = null;
            try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                reason = cancellationToken.IsCancellationRequested ? ReasonCodes.Canceled : "CORE_TIMEOUT";
                if (!await KillAndWaitAsync(process).ConfigureAwait(false))
                    return new(false, "CORE_CLEANUP_UNCERTAIN", "Owned validator termination is not confirmed; protected temporary input retained.");
            }
            try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (TimeoutException) { return new(false, "CORE_OUTPUT_UNCERTAIN", "Validator output pipes did not close."); }
            var summary = "Validator exit " + process.ExitCode.ToString(CultureInfo.InvariantCulture)
                + "; stdout characters " + stdout.Result.ToString(CultureInfo.InvariantCulture)
                + "; stderr characters " + stderr.Result.ToString(CultureInfo.InvariantCulture)
                + ". Raw output is not retained.";
            return new(reason is null && process.ExitCode == 0,
                reason ?? (process.ExitCode == 0 ? null : ReasonCodes.CoreConfigRejected), summary);
        }
        catch (OperationCanceledException) { return new(false, ReasonCodes.Canceled, "Validation canceled."); }
        finally
        {
            if (started && !process.HasExited) await KillAndWaitAsync(process).ConfigureAwait(false);
            if (!started || process.HasExited)
            {
                try { directory.Delete(recursive: true); }
                catch (IOException) { /* No false assertion of cleanup; temporary directory remains owner-scoped. */ }
            }
        }
    }

    internal static async Task<long> DrainAsync(StreamReader reader)
    {
        using var adapted = ProbeOutputDrain.AdaptOwnedProcessReader(reader);
        var decoded = adapted ?? reader;
        var buffer = new char[4096]; long total = 0;
        while (true)
        {
            var read = await decoded.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0) return total;
            total = total > long.MaxValue - read ? long.MaxValue : total + read;
        }
    }

    private static async Task<bool> KillAndWaitAsync(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            return process.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException) { return false; }
    }
}
