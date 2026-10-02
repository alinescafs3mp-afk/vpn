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
    private readonly CallerIdentity _caller;
    private readonly Task _loop;
    private NamedPipeServerStream? _current;

    private LocalIpcServer(string pipeName, IpcDispatcher dispatcher, BrokerEngine engine, CallerIdentity caller)
    {
        _pipeName = pipeName;
        _dispatcher = dispatcher;
        _engine = engine;
        _caller = caller;
        _loop = Task.Run(AcceptLoop);
    }

    public static LocalIpcServer Start(string pipeName, IpcDispatcher dispatcher, BrokerEngine engine, CallerIdentity localCaller)
    {
        return new LocalIpcServer(pipeName, dispatcher, engine, localCaller);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        NamedPipeServerStream? current;
        lock (_gate)
        {
            current = _current;
        }

        current?.Dispose();
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }

        _stop.Dispose();
    }

    public static async Task<IpcResponse?> RoundTripAsync(string pipeName, IpcRequest request, CancellationToken cancellationToken)
    {
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(3000, cancellationToken).ConfigureAwait(false);
        var frame = IpcFrames.Encode(request);
        await client.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        var header = new byte[4];
        if (!await ReadExactAsync(client, header, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > ProductLimits.MaxIpcFrameBytes)
        {
            return null;
        }

        var body = new byte[length];
        if (!await ReadExactAsync(client, body, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var full = new byte[4 + length];
        header.CopyTo(full, 0);
        body.CopyTo(full, 4);
        return IpcFrames.TryDecode<IpcResponse>(full, out var response, out _) ? response : null;
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or UnauthorizedAccessException)
            {
                if (_stop.IsCancellationRequested)
                {
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
                _current = server;
            }

            try
            {
                await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                await ServeOneAsync(server, _stop.Token).ConfigureAwait(false);
                await DrainUntilCloseAsync(server, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                if (_stop.IsCancellationRequested)
                {
                    return;
                }
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_current, server))
                    {
                        _current = null;
                    }
                }

                server.Dispose();
            }
        }
    }

    private async Task ServeOneAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(server, header, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > ProductLimits.MaxIpcFrameBytes)
        {
            return;
        }

        var body = new byte[length];
        if (!await ReadExactAsync(server, body, cancellationToken).ConfigureAwait(false))
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
            response = _dispatcher.Dispatch(request, _caller, incoming => _engine.HandleAsync(incoming, cancellationToken).GetAwaiter().GetResult());
        }

        var encoded = IpcFrames.Encode(response);
        await server.WriteAsync(encoded, cancellationToken).ConfigureAwait(false);
        await server.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DrainUntilCloseAsync(Stream stream, CancellationToken cancellationToken)
    {
        var scratch = new byte[1];
        while (true)
        {
            var count = await stream.ReadAsync(scratch, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return;
            }
        }
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
