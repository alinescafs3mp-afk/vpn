using System.ComponentModel;
using System.Text;
using System.Text.Json;
using AutoVpn.Contracts;
using AutoVpn.Infrastructure.WindowsService;

namespace AutoVpn.UnitTests;

public sealed class AstraV3FServiceQueryTests
{
    [Fact]
    public async Task SuccessfulExchangeRetainsEveryIdentityCheckBeforeAndAfterWrite()
    {
        var session = new FakeSession();
        var result = await InstalledServiceQuery.RunAsync(() => session);
        Assert.Equal("Ready", result.State);
        Assert.NotNull(result.Reply);
        Assert.Equal(new ServiceCheckDiagnostic(ServiceCheckStage.Complete, ServiceCheckFailure.None, null, true), result.Diagnostic);
        Assert.Equal(new[] { "OpenManager", "OpenService", "ConfigurationBefore", "StatusBefore", "ConnectPipe",
            "PipeProcess", "OpenProcess", "ProcessImage", "StatusLiveBefore", "LiveBefore", "SendRequest",
            "ReceiveReply", "StatusLiveAfter", "LiveAfter", "ConfigurationAfter", "Dispose" }, session.Calls);
        Assert.False(result.Reply.CanConnect || result.Reply.ProtectionArmed || result.Reply.CoreRunning);
    }

    [Theory]
    [InlineData("ConfigurationBefore", ServiceCheckStage.ConfigurationBefore, ServiceCheckFailure.ServiceExecutable)]
    [InlineData("PipeProcess", ServiceCheckStage.PipeProcess, ServiceCheckFailure.NativeCall)]
    [InlineData("OpenProcess", ServiceCheckStage.OpenProcess, ServiceCheckFailure.ProcessAccess)]
    [InlineData("ProcessImage", ServiceCheckStage.ProcessImage, ServiceCheckFailure.ProcessImageMismatch)]
    [InlineData("LiveBefore", ServiceCheckStage.ProcessBefore, ServiceCheckFailure.ProcessNotLive)]
    public async Task IdentityFailureNeverWritesAndPreservesItsExactStage(string call, ServiceCheckStage stage, ServiceCheckFailure failure)
    {
        var session = new FakeSession { OnCall = name => { if (name == call) throw new ServiceIdentityException(failure, 5); } };
        var result = await InstalledServiceQuery.RunAsync(() => session);
        Assert.Equal("AuthenticationFailed", result.State);
        Assert.Equal(new ServiceCheckDiagnostic(stage, failure, 5, false), result.Diagnostic);
        Assert.DoesNotContain("SendRequest", session.Calls);
        Assert.Equal("Dispose", session.Calls.Last());
        Assert.Null(result.Reply);
        Assert.Equal(5, result.NativeErrorCode);
        var expectedLegacy = stage switch
        {
            ServiceCheckStage.ConfigurationBefore => "SCM_CONFIGURATION",
            ServiceCheckStage.PipeProcess => "PIPE_PROCESS_QUERY",
            ServiceCheckStage.OpenProcess => "PROCESS_OPEN",
            ServiceCheckStage.ProcessImage => "PROCESS_IMAGE_MISMATCH",
            _ => "PROCESS_LIVENESS",
        };
        Assert.Equal(expectedLegacy, result.DiagnosticCode);
    }

    [Theory]
    [InlineData(1u, "Stopped")]
    [InlineData(2u, "Starting")]
    [InlineData(3u, "Stopping")]
    [InlineData(5u, "Resuming")]
    [InlineData(6u, "Pausing")]
    [InlineData(7u, "Paused")]
    [InlineData(99u, "Unavailable")]
    public async Task NonRunningServiceNeverConnectsToAPipe(uint state, string expected)
    {
        var session = new FakeSession { State = state };
        var result = await InstalledServiceQuery.RunAsync(() => session);
        Assert.Equal(expected, result.State);
        Assert.Equal(ServiceCheckStage.StatusBefore, result.Diagnostic!.Stage);
        Assert.False(result.Diagnostic.RequestMayHaveBeenSent);
        Assert.DoesNotContain("ConnectPipe", session.Calls);
        Assert.Null(result.Reply);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(uint.MaxValue)]
    public async Task InvalidPidNeverConnects(uint pid)
    {
        var session = new FakeSession { Pid = pid };
        var result = await InstalledServiceQuery.RunAsync(() => session);
        Assert.Equal(ServiceCheckFailure.InvalidProcessId, result.Diagnostic!.Failure);
        Assert.DoesNotContain("ConnectPipe", session.Calls);
    }

    [Fact]
    public async Task WrongServiceTypeIsRejectedBeforeConnecting()
    {
        var session = new FakeSession { ServiceType = 0x20 };
        var result = await InstalledServiceQuery.RunAsync(() => session);
        Assert.Equal(ServiceCheckFailure.ServiceType, result.Diagnostic!.Failure);
        Assert.DoesNotContain("ConnectPipe", session.Calls);
    }

    [Fact]
    public async Task ForeignPipePidNeverOpensAProcessOrWrites()
    {
        var session = new FakeSession { PipePid = 201 };
        var result = await InstalledServiceQuery.RunAsync(() => session);
        Assert.Equal(ServiceCheckFailure.PipeProcessMismatch, result.Diagnostic!.Failure);
        Assert.DoesNotContain("OpenProcess", session.Calls);
        Assert.DoesNotContain("SendRequest", session.Calls);
    }

    [Theory]
    [InlineData("OpenManager", 1060, "Unavailable", ServiceCheckStage.OpenManager)]
    [InlineData("OpenService", 1060, "NotInstalled", ServiceCheckStage.OpenService)]
    [InlineData("OpenService", 5, "AccessDenied", ServiceCheckStage.OpenService)]
    [InlineData("OpenManager", 5, "AccessDenied", ServiceCheckStage.OpenManager)]
    [InlineData("OpenService", 1722, "Unavailable", ServiceCheckStage.OpenService)]
    public async Task NativeErrorIsPreservedWithoutInventingAMissingService(string call, int error, string expected, ServiceCheckStage stage)
    {
        var session = new FakeSession { OnCall = name => { if (name == call) throw new Win32Exception(error, "PRIVATE_PATH_PASSWORD"); } };
        var result = await InstalledServiceQuery.RunAsync(() => session);
        Assert.Equal(expected, result.State);
        Assert.Equal(new ServiceCheckDiagnostic(stage, ServiceCheckFailure.NativeCall, error, false), result.Diagnostic);
        Assert.DoesNotContain("PRIVATE_PATH_PASSWORD", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("StatusLiveBefore", false)]
    [InlineData("StatusLiveAfter", true)]
    public async Task ChangedProcessIsNotAcceptedBeforeOrAfterRequest(string call, bool sent)
    {
        var session = new FakeSession();
        session.OnCall = name => { if (name == call) session.Pid++; };
        var result = await InstalledServiceQuery.RunAsync(() => session);
        Assert.Equal(sent ? "Unavailable" : "AuthenticationFailed", result.State);
        Assert.Equal(ServiceCheckFailure.ProcessChanged, result.Diagnostic!.Failure);
        Assert.Equal(sent, result.Diagnostic.RequestMayHaveBeenSent);
        Assert.Null(result.Reply);
    }

    [Theory]
    [InlineData("LiveAfter", ServiceCheckStage.ProcessAfter, ServiceCheckFailure.ProcessNotLive)]
    [InlineData("ConfigurationAfter", ServiceCheckStage.ConfigurationAfter, ServiceCheckFailure.ServiceAccount)]
    public async Task LateIdentityFailureDoesNotClaimNothingWasSent(string call, ServiceCheckStage stage, ServiceCheckFailure failure)
    {
        var session = new FakeSession { OnCall = name => { if (name == call) throw new ServiceIdentityException(failure); } };
        var result = await InstalledServiceQuery.RunAsync(() => session);
        Assert.Equal("Unavailable", result.State);
        Assert.Equal(new ServiceCheckDiagnostic(stage, failure, null, true), result.Diagnostic);
        Assert.Null(result.Reply);
        Assert.Equal("Dispose", session.Calls.Last());
    }

    [Theory]
    [InlineData(0, "Timeout", ServiceCheckFailure.DeadlineExceeded)]
    [InlineData(1, "AccessDenied", ServiceCheckFailure.AccessDenied)]
    [InlineData(2, "ProtocolError", ServiceCheckFailure.InvalidFrame)]
    [InlineData(3, "Unavailable", ServiceCheckFailure.TransportIo)]
    [InlineData(4, "Unavailable", ServiceCheckFailure.InvalidState)]
    [InlineData(5, "Unavailable", ServiceCheckFailure.UnexpectedCancellation)]
    public async Task ExpectedFaultsAreClassifiedWithoutLeakingExceptionText(int kind, string expected, ServiceCheckFailure reason)
    {
        var session = new FakeSession { OnCall = name =>
        {
            if (name != "ReceiveReply") return;
            throw kind switch
            {
                0 => new TimeoutException("PRIVATE_DIAGNOSTIC"), 1 => new UnauthorizedAccessException("PRIVATE_DIAGNOSTIC"),
                2 => new InvalidDataException("PRIVATE_DIAGNOSTIC"), 3 => new IOException("PRIVATE_DIAGNOSTIC"),
                4 => new InvalidOperationException("PRIVATE_DIAGNOSTIC"), _ => new OperationCanceledException("PRIVATE_DIAGNOSTIC"),
            };
        } };
        var result = await InstalledServiceQuery.RunAsync(() => session);
        Assert.Equal(expected, result.State);
        Assert.Equal(new ServiceCheckDiagnostic(ServiceCheckStage.ReceiveReply, reason, null, true), result.Diagnostic);
        Assert.DoesNotContain("PRIVATE_DIAGNOSTIC", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.Null(result.Reply);
        Assert.Equal("Dispose", session.Calls.Last());
    }

    [Fact]
    public async Task FailedWriteIsConservativelyMarkedAsPossiblySent()
    {
        var session = new FakeSession { OnCall = name => { if (name == "SendRequest") throw new IOException(); } };
        var result = await InstalledServiceQuery.RunAsync(() => session);
        Assert.True(result.Diagnostic!.RequestMayHaveBeenSent);
        Assert.Equal(ServiceCheckStage.SendRequest, result.Diagnostic.Stage);
        Assert.DoesNotContain("ReceiveReply", session.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task MismatchedReplyNeverPublishesReady(int mutation)
    {
        var session = new FakeSession { MutateReply = reply => mutation switch
        {
            0 => reply with { RequestId = Guid.NewGuid().ToString("N") },
            1 => reply with { ProcessId = reply.ProcessId + 1 },
            2 => reply with { CanConnect = true },
            _ => reply with { ProtectionArmed = true },
        } };
        var result = await InstalledServiceQuery.RunAsync(() => session);
        Assert.Equal("ProtocolError", result.State);
        Assert.Equal(ServiceCheckFailure.ReplyMismatch, result.Diagnostic!.Failure);
        Assert.Null(result.Reply);
    }

    [Theory]
    [InlineData("OpenManager")]
    [InlineData("ConnectPipe")]
    [InlineData("SendRequest")]
    [InlineData("ReceiveReply")]
    [InlineData("ConfigurationAfter")]
    public async Task CallerCancellationIsNeverAReadyOrTimeoutResult(string call)
    {
        using var stop = new CancellationTokenSource();
        var session = new FakeSession { OnCall = name => { if (name == call) stop.Cancel(); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InstalledServiceQuery.RunAsync(() => session, stop.Token));
        Assert.Equal("Dispose", session.Calls.Last());
    }

    [Fact]
    public async Task PreCanceledCallDoesNotCreateASession()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InstalledServiceQuery.RunAsync(
            () => throw new Exception("MUST_NOT_OPEN"), stop.Token));
    }

    [Fact]
    public async Task DeadlineCancelsAStalledConnectWithoutWriting()
    {
        var session = new FakeSession { StallConnect = true };
        var result = await InstalledServiceQuery.RunAsync(() => session);
        Assert.Equal("Timeout", result.State);
        Assert.Equal(new ServiceCheckDiagnostic(ServiceCheckStage.ConnectPipe, ServiceCheckFailure.DeadlineExceeded), result.Diagnostic);
        Assert.DoesNotContain("SendRequest", session.Calls);
        Assert.Equal("Dispose", session.Calls.Last());
    }

    private sealed class FakeSession : IInstalledServiceSession
    {
        public List<string> Calls { get; } = [];
        public Action<string>? OnCall { get; set; }
        public Func<ServiceStatusReply, ServiceStatusReply>? MutateReply { get; init; }
        public uint State { get; init; } = 4;
        public uint Pid { get; set; } = 200;
        public uint PipePid { get; init; } = 200;
        public uint ServiceType { get; init; } = 0x10;
        public bool StallConnect { get; init; }
        private int _configs, _statuses, _live;
        private Duplex? _stream;
        public Stream Pipe => _stream ??= new Duplex(this);
        private void Call(string name) { Calls.Add(name); OnCall?.Invoke(name); }
        public void OpenManager() => Call("OpenManager");
        public void OpenService() => Call("OpenService");
        public void ValidateConfiguration() => Call(++_configs == 1 ? "ConfigurationBefore" : "ConfigurationAfter");
        public ServiceProcessIdentity ReadStatus()
        {
            Call(++_statuses == 1 ? "StatusBefore" : _statuses == 2 ? "StatusLiveBefore" : "StatusLiveAfter");
            return new(ServiceType, State, Pid);
        }
        public async Task ConnectAsync(CancellationToken token)
        { Call("ConnectPipe"); if (StallConnect) await Task.Delay(Timeout.Infinite, token); token.ThrowIfCancellationRequested(); }
        public uint GetPipeProcessId() { Call("PipeProcess"); return PipePid; }
        public void OpenProcess(uint processId) { Assert.Equal(PipePid, processId); Call("OpenProcess"); }
        public void ValidateProcessImage() => Call("ProcessImage");
        public bool IsProcessRunning() { Call(++_live == 1 ? "LiveBefore" : "LiveAfter"); return true; }
        public void Dispose() { Call("Dispose"); _stream?.Dispose(); }
        private sealed class Duplex(FakeSession owner) : Stream
        {
            private MemoryStream? _response;
            private bool _read;
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
            {
                owner.Call("SendRequest"); token.ThrowIfCancellationRequested();
                var request = JsonSerializer.Deserialize<ServiceStatusRequest>(buffer.Span[4..], IpcJson.Options)!;
                var reply = InstalledServiceProtocol.Answer(request, Guid.NewGuid().ToString("N"), (int)owner.PipePid, 1);
                _response = new(ServiceStatusFrames.Encode(owner.MutateReply?.Invoke(reply) ?? reply));
                return ValueTask.CompletedTask;
            }
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
            {
                if (!_read) { _read = true; owner.Call("ReceiveReply"); }
                return _response!.ReadAsync(buffer, token);
            }
            protected override void Dispose(bool disposing) { if (disposing) _response?.Dispose(); base.Dispose(disposing); }
            public override bool CanRead => true;
            public override bool CanWrite => true;
            public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
