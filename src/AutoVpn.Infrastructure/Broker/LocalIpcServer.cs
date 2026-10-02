using System.Buffers.Binary;
using System.IO.Pipes;
using AutoVpn.Application;
using AutoVpn.Contracts;
using AutoVpn.Domain;

namespace AutoVpn.Infrastructure.Broker;

public sealed class LocalIpcServer : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly string _pipeName;
    private readonly IpcDispatcher _dispatcher;
    private readonly BrokerEngine _engine;
    private readonly Func<string, NamedPipeServerStream> _open;
    private readonly List<NamedPipeServerStream> _streams = [];
    private readonly List<Task> _sessions = [];
    private readonly Task _loop;
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private LocalIpcServer(string pipeName, IpcDispatcher dispatcher, BrokerEngine engine, Func<string, NamedPipeServerStream> open)
    {
        _pipeName = pipeName;
        _dispatcher = dispatcher;
        _engine = engine;
        _open = open;
        _loop = Task.Run(AcceptLoop);
    }

    public string? PipeFault { get; private set; }

    public int RetainedSessionCount
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Count;
            }
        }
    }

    public int ActiveStreamCount => ActiveStreams();

    public Task Completion => _completed.Task;

    public static LocalIpcServer Start(
        string pipeName,
        IpcDispatcher dispatcher,
        BrokerEngine engine,
        CallerIdentity localCaller,
        Func<string, NamedPipeServerStream>? openPipe = null)
    {
        _ = localCaller;
        return new LocalIpcServer(pipeName, dispatcher, engine, openPipe ?? OpenDefault);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or TimeoutException)
        {
        }

        NamedPipeServerStream[] streams;
        Task[] sessions;
        lock (_gate)
        {
            streams = _streams.ToArray();
            sessions = _sessions.ToArray();
        }

        foreach (var stream in streams)
        {
            stream.Dispose();
        }

        try
        {
            await Task.WhenAll(sessions).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or TimeoutException)
        {
        }

        _completed.TrySetResult();
        _stop.Dispose();
    }

    public static async Task<IpcResponse?> RoundTripAsync(string pipeName, IpcRequest request, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(ProductLimits.IpcRoundTripTimeoutMs);
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(3000, budget.Token).ConfigureAwait(false);
            var frame = IpcFrames.Encode(request);
            await client.WriteAsync(frame, budget.Token).ConfigureAwait(false);
            var header = new byte[4];
            if (!await ReadExactAsync(client, header, budget.Token).ConfigureAwait(false))
            {
                return null;
            }

            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length <= 0 || length > ProductLimits.MaxIpcFrameBytes)
            {
                return null;
            }

            var body = new byte[length];
            if (!await ReadExactAsync(client, body, budget.Token).ConfigureAwait(false))
            {
                return null;
            }

            var full = new byte[4 + length];
            header.CopyTo(full, 0);
            body.CopyTo(full, 4);
            return IpcFrames.TryDecode<IpcResponse>(full, out var response, out _) ? response : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Ответ службы не пришёл в отведённое время.");
        }
    }

    private async Task AcceptLoop()
    {
        var faults = 0;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                PruneSessions();
                if (ActiveStreams() >= ProductLimits.IpcPipeInstances)
                {
                    try
                    {
                        await Task.Delay(50, _stop.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    continue;
                }

                NamedPipeServerStream server;
                try
                {
                    server = _open(_pipeName);
                    faults = 0;
                }
                catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or UnauthorizedAccessException)
                {
                    if (_stop.IsCancellationRequested)
                    {
                        return;
                    }

                    faults++;
                    if (faults >= ProductLimits.IpcPipeCreateAttempts)
                    {
                        PipeFault = ex.GetType().Name;
                        return;
                    }

                    try
                    {
                        await Task.Delay(50, _stop.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    continue;
                }

                lock (_gate)
                {
                    _streams.Add(server);
                }

                try
                {
                    await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Forget(server);
                    server.Dispose();
                    return;
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    Forget(server);
                    server.Dispose();
                    if (_stop.IsCancellationRequested)
                    {
                        return;
                    }

                    continue;
                }

                var session = ServeAndCloseAsync(server);
                lock (_gate)
                {
                    PruneSessionsUnlocked();
                    _sessions.Add(session);
                }
            }
        }
        finally
        {
            _completed.TrySetResult();
        }
    }

    private async Task ServeAndCloseAsync(NamedPipeServerStream server)
    {
        await Task.Yield();
        try
        {
            await ServeOneAsync(server).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or TimeoutException)
        {
        }
        finally
        {
            Forget(server);
            server.Dispose();
            PruneSessions();
        }
    }

    private async Task ServeOneAsync(NamedPipeServerStream server)
    {
        using var readBudget = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        readBudget.CancelAfter(ProductLimits.IpcFrameTimeoutMs);
        var header = new byte[4];
        if (!await ReadExactAsync(server, header, readBudget.Token).ConfigureAwait(false))
        {
            return;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > ProductLimits.MaxIpcFrameBytes)
        {
            return;
        }

        var body = new byte[length];
        if (!await ReadExactAsync(server, body, readBudget.Token).ConfigureAwait(false))
        {
            return;
        }

        var full = new byte[4 + length];
        header.CopyTo(full, 0);
        body.CopyTo(full, 4);
        IpcResponse response;
        if (!IpcFrames.TryDecode<IpcRequest>(full, out var request, out _) || request is null)
        {
            response = new IpcResponse { RequestId = "", Ok = false, ErrorCode = "MALFORMED", Message = "Запрос повреждён." };
        }
        else
        {
            var peer = PipePeer.Inspect(server);
            if (!peer.Accepted || !peer.Verified)
            {
                response = new IpcResponse
                {
                    RequestId = request.RequestId,
                    Ok = false,
                    ErrorCode = "PEER",
                    Message = OperatingSystem.IsWindows()
                        ? "Личность клиента Windows не проверена. Канал отклонён."
                        : "Владелец канала не совпадает с пользователем службы.",
                };
            }
            else
            {
                var caller = new CallerIdentity { Sid = peer.Identity, SessionId = 0, IsRemotePipe = false };
                try
                {
                    response = _dispatcher.Dispatch(request, caller, incoming => _engine.HandleAsync(incoming, _stop.Token).GetAwaiter().GetResult());
                }
                catch (Exception)
                {
                    response = new IpcResponse
                    {
                        RequestId = request.RequestId,
                        Ok = false,
                        ErrorCode = "INTERNAL",
                        Message = "Запрос отклонён.",
                    };
                }
            }
        }

        using var writeBudget = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        writeBudget.CancelAfter(ProductLimits.IpcWriteTimeoutMs);
        var encoded = IpcFrames.Encode(response);
        await server.WriteAsync(encoded, writeBudget.Token).ConfigureAwait(false);
        await server.FlushAsync(writeBudget.Token).ConfigureAwait(false);
    }

    private void Forget(NamedPipeServerStream server)
    {
        lock (_gate)
        {
            _streams.Remove(server);
        }
    }

    private int ActiveStreams()
    {
        lock (_gate)
        {
            return _streams.Count;
        }
    }

    private void PruneSessions()
    {
        lock (_gate)
        {
            PruneSessionsUnlocked();
        }
    }

    private void PruneSessionsUnlocked()
    {
        _sessions.RemoveAll(session => session.IsCompleted);
    }

    private static NamedPipeServerStream OpenDefault(string pipeName)
    {
        return new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            ProductLimits.IpcPipeInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    private static async Task<bool> ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return false;
            }

            read += count;
        }

        return true;
    }
}
