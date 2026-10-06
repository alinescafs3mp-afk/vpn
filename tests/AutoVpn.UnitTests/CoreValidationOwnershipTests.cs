using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Core;
using Xunit.Abstractions;

namespace AutoVpn.UnitTests;

public sealed class CoreValidationOwnershipTests(ITestOutputHelper trace)
{
    private const string PrivateMarker = "SYNTHETIC_PRIVATE_VALIDATOR_пароль🙂";

    [Theory]
    [InlineData(0)]
    [InlineData(23)]
    public async Task ActualValidatorExitAndBothOutputsAreJoinedBeforeSuccessfulCleanup(int exitCode)
    {
        var fixture = new ValidatorFixture(trace, "finite-exit-" + exitCode.ToString(CultureInfo.InvariantCulture));
        await OwnedFixtureExecution.RunAsync(fixture, async () =>
        {
            fixture.Start(exitCode);
            await fixture.WaitReadyAsync();
            var child = Assert.IsType<Process>(fixture.Child);
            fixture.Release();
            var result = await fixture.ResultAsync();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(exitCode, child.ExitCode);
            Assert.Equal(exitCode == 0, result.Ok);
            Assert.Equal(exitCode == 0 ? null : ReasonCodes.CoreConfigRejected, result.ReasonCode);
            Assert.Null(result.PendingCleanup);
            var report = Assert.IsType<OwnedProcessCleanupReport>(result.CleanupReport);
            Assert.True(report.Complete, report.Summary);
            Assert.True(report.OutputHealthy, report.Summary);
            Assert.Equal(OwnedOutputState.Eof, report.Stdout.State);
            Assert.Equal(OwnedOutputState.Eof, report.Stderr.State);
            Assert.False(Directory.Exists(fixture.ValidatorDirectory));
            var characters = 6L * ("OUT:" + PrivateMarker + "🙂\\literal\n" + new string('x', 8192)).Length;
            Assert.Contains("stdout characters " + characters.ToString(CultureInfo.InvariantCulture), result.RedactedOutput, StringComparison.Ordinal);
            Assert.Contains("stderr characters " + characters.ToString(CultureInfo.InvariantCulture), result.RedactedOutput, StringComparison.Ordinal);
            fixture.AssertSanitized(result);
            await fixture.AssertBinaryReleasedAsync();
            Evidence("finite-exit-" + exitCode.ToString(CultureInfo.InvariantCulture), result, child.HasExited,
                binaryExclusiveOpen: true);
        });
    }

    [Fact]
    public async Task CancellationAfterActualChildReadyJoinsExitReadersAndInputCleanup()
    {
        var fixture = new ValidatorFixture(trace, "canceled-after-ready");
        await OwnedFixtureExecution.RunAsync(fixture, async () =>
        {
            using var stop = new CancellationTokenSource();
            fixture.Start(0, stop.Token);
            await fixture.WaitReadyAsync();
            var child = Assert.IsType<Process>(fixture.Child);
            stop.Cancel();
            var result = await fixture.ResultAsync();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(child.HasExited);
            Assert.False(result.Ok);
            Assert.Equal(ReasonCodes.Canceled, result.ValidationReasonCode);
            Assert.Equal(ReasonCodes.Canceled, result.ReasonCode);
            Assert.Null(result.PendingCleanup);
            var report = Assert.IsType<OwnedProcessCleanupReport>(result.CleanupReport);
            Assert.True(report.Complete, report.Summary);
            Assert.True(report.OutputHealthy, report.Summary);
            Assert.Equal(OwnedOutputState.Eof, report.Stdout.State);
            Assert.Equal(OwnedOutputState.Eof, report.Stderr.State);
            Assert.False(Directory.Exists(fixture.ValidatorDirectory));
            await fixture.AssertBinaryReleasedAsync();
            fixture.AssertSanitized(result);
            Evidence("canceled-after-ready", result, child.HasExited, binaryExclusiveOpen: true);
        });
    }

    [WindowsHandleFact]
    public async Task ExitedValidatorRetainsBinaryLockUntilLockedInputCleanupIsRetried()
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Windows-only test was not skipped.");
        var fixture = new ValidatorFixture(trace, "windows-retained-delete-lock");
        await OwnedFixtureExecution.RunAsync(fixture, async () =>
        {
            FileStream? held = null;
            try
            {
                fixture.Start(0);
                await fixture.WaitReadyAsync();
                var child = Assert.IsType<Process>(fixture.Child);
                held = new FileStream(fixture.ConfigPath!, FileMode.Open, FileAccess.Read, FileShare.Read);
                fixture.Release();
                var result = await fixture.ResultAsync();
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(child.HasExited);
                Assert.Equal(0, child.ExitCode);
                Assert.False(result.Ok);
                Assert.Equal("CORE_CLEANUP_UNCERTAIN", result.ReasonCode);
                var report = Assert.IsType<OwnedProcessCleanupReport>(result.CleanupReport);
                Assert.Equal("DIRECTORY_CLEANUP_FAILED", report.Phase);
                Assert.True(report.ProcessExitConfirmed);
                Assert.True(report.ReadersJoined);
                Assert.True(report.OutputHealthy);
                Assert.False(report.DirectoryRemoved);
                Assert.False(report.ResourcesReleased);
                Assert.False(report.Complete);
                Assert.Contains(report.ExceptionKind, new[] { "IO", "ACCESS" });
                Assert.True(Directory.Exists(fixture.ValidatorDirectory));
                Assert.True(File.Exists(fixture.ConfigPath));
                // Child exit is confirmed first: its executable image is no longer the proof.
                Assert.Throws<IOException>(() =>
                { using var writer = File.Open(fixture.BinaryPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); });
                Assert.Throws<IOException>(() => File.Delete(fixture.BinaryPath));
                fixture.AssertSanitized(result);
                var pending = Assert.IsAssignableFrom<IOwnedProcessCleanup>(result.PendingCleanup);
                Assert.Same(report, pending.LastReport);
                held.Dispose(); held = null;
                var retry = await pending.RetryAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(retry.Complete, retry.Summary);
                Assert.True(retry.OutputHealthy);
                Assert.Same(retry, pending.LastReport);
                Assert.False(Directory.Exists(fixture.ValidatorDirectory));
                Assert.False(result.Ok);
                Assert.Same(report, result.CleanupReport);
                Assert.Equal("DIRECTORY_CLEANUP_FAILED", result.CleanupReport!.Phase);
                fixture.AssertSanitized(result);
                await fixture.AssertBinaryReleasedAsync();
                File.Delete(fixture.BinaryPath);
                Assert.False(File.Exists(fixture.BinaryPath));
                Evidence("windows-retained-delete-lock", result, child.HasExited,
                    binaryExclusiveOpen: true, binaryWriteDeniedBeforeRetry: true, binaryDeleteDeniedBeforeRetry: true,
                    binaryDeletedAfterRetry: true, retryComplete: retry.Complete,
                    immutableOriginal: !result.Ok && ReferenceEquals(report, result.CleanupReport));
            }
            finally { held?.Dispose(); }
        });
    }

    [Fact]
    public async Task ActualInvalidUtf8IsAJoinedReadFailureAndCannotBecomeValidationSuccess()
    {
        var fixture = new ValidatorFixture(trace, "invalid-utf8-settled-fault");
        await OwnedFixtureExecution.RunAsync(fixture, async () =>
        {
            fixture.Start(0, mode: "invalid-utf8");
            await fixture.WaitReadyAsync();
            var child = Assert.IsType<Process>(fixture.Child);
            fixture.Release();
            var result = await fixture.ResultAsync();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(child.HasExited);
            Assert.Equal(0, child.ExitCode);
            Assert.False(result.Ok);
            Assert.Equal("CORE_OUTPUT_FAILED", result.ReasonCode);
            Assert.Null(result.ValidationReasonCode);
            Assert.Null(result.PendingCleanup);
            var report = Assert.IsType<OwnedProcessCleanupReport>(result.CleanupReport);
            Assert.True(report.Complete, report.Summary);
            Assert.False(report.OutputHealthy);
            Assert.Equal(OwnedOutputState.Failed, report.Stdout.State);
            Assert.Equal("DECODING", report.Stdout.ExceptionKind);
            Assert.Equal(OwnedOutputState.Eof, report.Stderr.State);
            Assert.False(Directory.Exists(fixture.ValidatorDirectory));
            await fixture.AssertBinaryReleasedAsync();
            fixture.AssertSanitized(result);
            Evidence("invalid-utf8-settled-fault", result, child.HasExited, binaryExclusiveOpen: true);
        });
    }

    private void Evidence(string scenario, CoreValidationResult result, bool processExited, bool binaryExclusiveOpen,
        bool? binaryWriteDeniedBeforeRetry = null, bool? binaryDeleteDeniedBeforeRetry = null,
        bool? binaryDeletedAfterRetry = null, bool? retryComplete = null, bool? immutableOriginal = null) =>
        trace.WriteLine(JsonSerializer.Serialize(new
        {
            scenario, ok = result.Ok, reason = result.ReasonCode, validationReason = result.ValidationReasonCode,
            cleanup = result.CleanupReport, processExited, binaryExclusiveOpen,
            binaryReleaseProbe = OperatingSystem.IsWindows() ? "EXCLUSIVE_WRITE" : "EXCLUSIVE_READ",
            binaryWritable = OperatingSystem.IsWindows() ? (bool?)binaryExclusiveOpen : null,
            binaryReadDeniedBeforeCleanup = OperatingSystem.IsWindows() ? (bool?)null : true,
            binaryWriteDeniedBeforeRetry,
            binaryDeleteDeniedBeforeRetry, binaryDeletedAfterRetry, retryComplete, immutableOriginal,
        }));

    private sealed class ValidatorFixture : IAsyncDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("autovpn-validator-owned-");
        private readonly string _control;
        private readonly string _hash;
        private readonly ITestOutputHelper _trace;
        private readonly string _scenario;
        private Task<CoreValidationResult>? _validation;
        private CoreValidationResult? _result;
        private FileUseProcessIdentity? _childIdentity;
        internal string BinaryPath { get; }
        internal string? ConfigPath { get; private set; }
        internal string? ValidatorDirectory { get; private set; }
        internal Process? Child { get; private set; }

        internal ValidatorFixture(ITestOutputHelper trace, string scenario)
        {
            _trace = trace; _scenario = scenario;
            var source = Path.Combine(AppContext.BaseDirectory, "process-fixture");
            var binaryDirectory = Path.Combine(_root.FullName, "bin");
            try
            {
                Directory.CreateDirectory(binaryDirectory);
                _control = Directory.CreateDirectory(Path.Combine(_root.FullName, "control")).FullName;
                foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                {
                    var destination = Path.Combine(binaryDirectory, Path.GetRelativePath(source, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(file, destination);
                }
                BinaryPath = Path.Combine(binaryDirectory, OperatingSystem.IsWindows()
                    ? "AutoVpn.ProcessFixture.exe" : "AutoVpn.ProcessFixture");
                if (!File.Exists(BinaryPath)) throw new InvalidOperationException("VALIDATOR_APPHOST_MISSING");
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(BinaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                _hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(BinaryPath)));
            }
            catch (Exception original)
            {
                try { _root.Delete(recursive: true); }
                catch (Exception cleanup) { throw new AggregateException(original, cleanup); }
                throw;
            }
        }

        internal void Start(int exitCode, CancellationToken cancellationToken = default, string mode = "finite-output")
        {
            var profile = JsonSerializer.Serialize(new
            {
                fixture = "owned-validator-v1", mode, exitCode,
                marker = PrivateMarker, controlDirectory = _control, tun = new { enable = false },
            });
            Assert.False(MihomoProfileGenerator.EnablesTun(profile));
            _validation = MihomoProcessController.ValidateAsync(BinaryPath, _hash, profile, cancellationToken);
        }

        internal async Task WaitReadyAsync()
        {
            var ready = Path.Combine(_control, "ready.json");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(ready))
            {
                if (_validation!.IsCompleted)
                {
                    _result = await _validation;
                    Assert.Fail("Validator exited before its ready marker: " + _result.ReasonCode + "; " + _result.RedactedOutput);
                }
                await Task.Delay(10, deadline.Token);
            }
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(ready, deadline.Token));
            ConfigPath = json.RootElement.GetProperty("configPath").GetString()!;
            ValidatorDirectory = json.RootElement.GetProperty("directoryPath").GetString()!;
            Child = Process.GetProcessById(json.RootElement.GetProperty("processId").GetInt32());
            _ = Child.Handle;
            _childIdentity = WindowsFileUseDiagnostics.CaptureIdentity(Child);
            Assert.False(Child.HasExited);
            Assert.Equal(Path.Combine(ValidatorDirectory!, "config.yaml"), ConfigPath);
            if (!OperatingSystem.IsWindows())
            {
                // Confirm this filesystem enforces the validator's advisory
                // FileStream lock before relying on the matching release probe.
                Assert.Throws<IOException>(() =>
                { using var reader = File.Open(BinaryPath, FileMode.Open, FileAccess.Read, FileShare.None); });
            }
        }

        internal async Task AssertBinaryReleasedAsync()
        {
            // Persist the original result BEFORE the one-shot platform probe.
            // No sleep, retry, RM query, or extra file open precedes this assertion.
            Observation("BEFORE_BINARY_PROBE");
            // Linux write-open also depends on the kernel executable-image
            // lifetime (ETXTBSY), separate from our verified FileStream lock.
            var access = OperatingSystem.IsWindows() ? FileAccess.Write : FileAccess.Read;
            try
            {
                using var exclusive = File.Open(BinaryPath, FileMode.Open, access, FileShare.None);
            }
            catch (Exception original)
            {
                var diagnosticCleanup = await ObserveFileFailureAsync("BINARY_PROBE_FAILED", original);
                if (diagnosticCleanup is not null) throw new AggregateException(original, diagnosticCleanup);
                throw;
            }
            Observation("BINARY_PROBE_SUCCEEDED");
            var release = Assert.IsType<OwnedResourceReleaseReport>(
                (_result?.PendingCleanup?.LastReport ?? _result?.CleanupReport)?.ReleaseObservation);
            Assert.True(release.ProcessPresent);
            Assert.True(release.BinaryPresent);
            Assert.True(release.ProcessDisposeReturned);
            Assert.True(release.BinaryDisposeReturned);
            Assert.True(release.BinarySafeHandleClosed);
            // IsInvalid is independent of closed: do not require a sentinel value.
        }

        private void Observation(string stage, Exception? error = null, FileUseDiagnosticReport? fileUse = null) =>
            _trace.WriteLine(JsonSerializer.Serialize(new
            {
                evidence = "validator-file-lifetime-v1", scenario = _scenario, stage,
                ok = _result?.Ok, reason = _result?.ReasonCode,
                validationReason = _result?.ValidationReasonCode,
                originalCleanup = _result?.CleanupReport,
                latestCleanup = _result?.PendingCleanup?.LastReport ?? _result?.CleanupReport,
                exceptionKind = OwnedProcessCleanup.Kind(error), hResult = error?.HResult ?? 0,
                fileUse,
            }));

        private async Task<Exception?> ObserveFileFailureAsync(string stage, Exception original)
        {
            try
            {
                Observation(stage, original);
                var diagnostic = await WindowsFileUseDiagnostics.CaptureAsync(BinaryPath, Child, _childIdentity);
                Observation(stage + "_FILE_USE", original, diagnostic);
                // A diagnostic timeout/error remains metadata. Incomplete cleanup
                // retains its capability alongside, never instead of, the original.
                return diagnostic.RetainedCleanupFailure;
            }
            catch (Exception diagnosticFailure) { return diagnosticFailure; }
        }

        internal void Release() => File.WriteAllText(Path.Combine(_control, "release.txt"), "release synthetic validator");

        internal async Task<CoreValidationResult> ResultAsync()
        {
            _result = await _validation!.WaitAsync(TimeSpan.FromSeconds(35));
            Observation("VALIDATION_RETURNED");
            return _result;
        }

        internal void AssertSanitized(CoreValidationResult result)
        {
            foreach (var text in new[] { result.ToString(), result.RedactedOutput, JsonSerializer.Serialize(result) })
            {
                Assert.DoesNotContain("SYNTHETIC_PRIVATE_VALIDATOR_", text, StringComparison.Ordinal);
                Assert.DoesNotContain(Path.GetFileName(_root.FullName), text, StringComparison.Ordinal);
                Assert.DoesNotContain(Path.GetFileName(ValidatorDirectory!), text, StringComparison.Ordinal);
                foreach (var value in new[] { PrivateMarker, _root.FullName, BinaryPath, ConfigPath!, ValidatorDirectory! })
                    Assert.DoesNotContain(value, text, StringComparison.Ordinal);
            }
            Assert.DoesNotContain("PendingCleanup", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        }

        public async ValueTask DisposeAsync()
        {
            Release();
            if (_validation is not null) _result = await _validation.WaitAsync(TimeSpan.FromSeconds(35));
            if (_result?.PendingCleanup is { } pending)
            {
                OwnedProcessCleanupReport report;
                try { report = await pending.RetryAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception error)
                {
                    throw new AggregateException(error,
                        new OwnedProcessCleanupException(pending, pending.LastReport ?? _result.CleanupReport!));
                }
                if (!report.Complete) throw new OwnedProcessCleanupException(pending, report);
            }
            if (Child is not null) await Child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            // No destructive finally: a timeout or incomplete retry keeps these
            // fixture resources intact along with the returned cleanup capability.
            Child?.Dispose();
            Observation("BEFORE_FIXTURE_DIRECTORY_DELETE");
            try { _root.Delete(recursive: true); }
            catch (Exception original)
            {
                var diagnosticCleanup = await ObserveFileFailureAsync("FIXTURE_DIRECTORY_DELETE_FAILED", original);
                if (diagnosticCleanup is not null) throw new AggregateException(original, diagnosticCleanup);
                throw;
            }
            Observation("FIXTURE_DIRECTORY_DELETE_SUCCEEDED");
        }
    }
}
