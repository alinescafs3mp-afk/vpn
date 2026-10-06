using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Probe;

namespace AutoVpn.Infrastructure.Core;

public sealed record CoreValidationResult(bool Ok, string? ReasonCode, string RedactedOutput)
{
    // These are the original operation and cleanup snapshots. A successful
    // explicit retry never upgrades the validation that already failed.
    public string? ValidationReasonCode { get; internal init; } = ReasonCode;
    public OwnedProcessCleanupReport? CleanupReport { get; internal init; }
    [JsonIgnore] public IOwnedProcessCleanup? PendingCleanup { get; internal init; }
}

public static class MihomoProcessController
{
    public static async Task<CoreValidationResult> ValidateAsync(string binaryPath, string expectedSha256,
        string yaml, CancellationToken cancellationToken)
    {
        if (MihomoProfileGenerator.EnablesTun(yaml) && !OperatingSystem.IsWindows())
            return new(false, ReasonCodes.NotWindows, "TUN validation is refused on this operating system.");
        if (!File.Exists(binaryPath)) return new(false, "CORE_MISSING", "Pinned core binary is not present.");
        if (cancellationToken.IsCancellationRequested) return new(false, ReasonCodes.Canceled, "Validation canceled before launch.");
        var resources = new NativeProcessCleanupResources();
        Task<long>? stdout = null; Task<long>? stderr = null;
        CoreValidationResult validation;
        var phase = "BINARY_OPEN";
        try
        {
            // Retain the exact non-delete-sharing handle even after child exit
            // when a reader or directory cleanup still needs an explicit retry.
            resources.Binary = new FileStream(binaryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            phase = "BINARY_HASH";
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(resources.Binary, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                validation = new(false, "CORE_HASH", "Pinned core hash does not match.");
            else
            {
                phase = "DIRECTORY_CREATE";
                resources.Directory = Directory.CreateTempSubdirectory("autovpn-validate-");
                var directory = resources.Directory;
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                phase = "CONFIG_WRITE";
                var config = Path.Combine(directory.FullName, "config.yaml");
                await File.WriteAllTextAsync(config, yaml, cancellationToken).ConfigureAwait(false);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(config, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                phase = "PROCESS_START";
                resources.Process = new Process();
                var process = resources.Process;
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = Path.GetFullPath(binaryPath), WorkingDirectory = directory.FullName,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false, true),
                    StandardErrorEncoding = new UTF8Encoding(false, true),
                    UseShellExecute = false, CreateNoWindow = true,
                };
                foreach (var argument in new[] { "-t", "-f", config, "-d", directory.FullName }) process.StartInfo.ArgumentList.Add(argument);
                process.StartInfo.Environment["HOME"] = directory.FullName;
                cancellationToken.ThrowIfCancellationRequested();
                resources.Started = process.Start();
                if (!resources.Started)
                    validation = new(false, ReasonCodes.CoreConfigRejected, "Core process did not start.");
                else
                {
                    phase = "OUTPUT_START";
                    // Drain actual EOF without retaining raw, possibly secret, output.
                    resources.Stdout = stdout = DrainAsync(process.StandardOutput);
                    resources.Stderr = stderr = DrainAsync(process.StandardError);
                    phase = "VALIDATION_WAIT";
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    deadline.CancelAfter(TimeSpan.FromSeconds(20));
                    try
                    {
                        await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                        var exit = process.ExitCode;
                        validation = new(exit == 0, exit == 0 ? null : ReasonCodes.CoreConfigRejected,
                            "Validator exit " + exit.ToString(CultureInfo.InvariantCulture) + ".");
                    }
                    catch (OperationCanceledException)
                    {
                        validation = new(false, cancellationToken.IsCancellationRequested ? ReasonCodes.Canceled : "CORE_TIMEOUT",
                            "Validation wait canceled; owned cleanup remains required.");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            validation = new(false, ReasonCodes.Canceled, "Validation canceled during " + phase + ".");
        }
        catch (Exception error)
        {
            validation = new(false, "CORE_VALIDATION_FAILED", "Validation phase " + phase + " failed; error "
                + OwnedProcessCleanup.Kind(error) + ":" + error.HResult.ToString("X8", CultureInfo.InvariantCulture) + ".");
        }

        // Preparation and the validation wait have joined before ownership is
        // handed off. One attempt: 5 s for exit, then the existing 3 s for output.
        var cleanup = new OwnedProcessCleanup(resources, TimeSpan.FromSeconds(3));
        var report = await cleanup.RetryAsync().ConfigureAwait(false);
        var reason = !report.Complete
            ? report.Phase == "OUTPUT_JOIN_FAILED" ? "CORE_OUTPUT_UNCERTAIN" : "CORE_CLEANUP_UNCERTAIN"
            : !report.OutputHealthy ? "CORE_OUTPUT_FAILED" : validation.ReasonCode;
        var summary = validation.RedactedOutput;
        if (stdout?.Status == TaskStatus.RanToCompletion && stderr?.Status == TaskStatus.RanToCompletion)
            summary += " stdout characters " + stdout.Result.ToString(CultureInfo.InvariantCulture)
                + "; stderr characters " + stderr.Result.ToString(CultureInfo.InvariantCulture) + ".";
        return new(validation.Ok && report.Complete && report.OutputHealthy, reason,
            summary + " Raw output is not retained. Cleanup: " + report.Summary)
        {
            ValidationReasonCode = validation.ReasonCode, CleanupReport = report,
            PendingCleanup = report.Complete ? null : cleanup,
        };
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
}
