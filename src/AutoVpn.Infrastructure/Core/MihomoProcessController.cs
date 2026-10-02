using System.Diagnostics;
using AutoVpn.Domain;

namespace AutoVpn.Infrastructure.Core;

public sealed record CoreValidationResult(bool Ok, string? ReasonCode, string RedactedOutput);

public static class MihomoProcessController
{
    public static async Task<CoreValidationResult> ValidateAsync(string binaryPath, string expectedSha256, string yaml, CancellationToken cancellationToken)
    {
        if (MihomoProfileGenerator.EnablesTun(yaml) && !OperatingSystem.IsWindows())
        {
            return new CoreValidationResult(false, ReasonCodes.NotWindows, "TUN validation is refused on this operating system.");
        }

        if (!File.Exists(binaryPath))
        {
            return new CoreValidationResult(false, "CORE_MISSING", "Pinned core binary is not present.");
        }

        var bytes = await File.ReadAllBytesAsync(binaryPath, cancellationToken).ConfigureAwait(false);
        var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return new CoreValidationResult(false, "CORE_HASH", "Pinned core hash does not match.");
        }

        var directory = Directory.CreateTempSubdirectory("autovpn-validate-");
        try
        {
            var config = Path.Combine(directory.FullName, "config.yaml");
            await File.WriteAllTextAsync(config, yaml, cancellationToken).ConfigureAwait(false);
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = Path.GetFullPath(binaryPath),
                WorkingDirectory = directory.FullName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            process.StartInfo.ArgumentList.Add("-t");
            process.StartInfo.ArgumentList.Add("-f");
            process.StartInfo.ArgumentList.Add(config);
            process.StartInfo.ArgumentList.Add("-d");
            process.StartInfo.ArgumentList.Add(directory.FullName);
            process.StartInfo.Environment["HOME"] = directory.FullName;
            if (!process.Start())
            {
                return new CoreValidationResult(false, ReasonCodes.CoreConfigRejected, "Core process did not start.");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                return new CoreValidationResult(false, "CORE_TIMEOUT", "Core validation timed out.");
            }

            var output = SecretRedactor.Redact(await outputTask.ConfigureAwait(false) + await errorTask.ConfigureAwait(false));
            return process.ExitCode == 0
                ? new CoreValidationResult(true, null, output)
                : new CoreValidationResult(false, ReasonCodes.CoreConfigRejected, output);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
