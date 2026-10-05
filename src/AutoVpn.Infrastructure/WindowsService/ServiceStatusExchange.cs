using AutoVpn.Contracts;

namespace AutoVpn.Infrastructure.WindowsService;

/// <summary>One bounded, read-only exchange. Malformed client input does not terminate the listener.</summary>
public static class ServiceStatusExchange
{
    /// <summary>
    /// Retain the reply until the peer closes, using the caller's existing deadline.
    /// PipeStream.Flush is not an acknowledgement; DisconnectNamedPipe drops unread bytes.
    /// Do not use unbounded WaitForPipeDrain or extend the frame budget for slow clients.
    /// </summary>
    public static async Task<bool> HandleConnectionAsync(Stream stream, Func<bool> isAuthorized,
        string instanceId, int processId, Func<long> uptimeSeconds, CancellationToken token)
    {
        if (!await HandleAsync(stream, isAuthorized, instanceId, processId, uptimeSeconds, token).ConfigureAwait(false))
            return false;
        try
        {
            // Exactly one request per connection. The client disposes its pipe after reading.
            // Extra bytes are refused; idle peers remain bounded by the original token.
            return await stream.ReadAsync(new byte[1], token).ConfigureAwait(false) == 0;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            return false;
        }
    }

    public static async Task<bool> HandleAsync(Stream stream, Func<bool> isAuthorized,
        string instanceId, int processId, Func<long> uptimeSeconds, CancellationToken token)
    {
        try
        {
            var request = await ServiceStatusFrames.ReadAsync<ServiceStatusRequest>(stream, token).ConfigureAwait(false);
            // Windows pipe identity inspection requires a message to have been read first.
            if (!isAuthorized()) return false;
            var reply = InstalledServiceProtocol.Answer(request, instanceId, processId, uptimeSeconds());
            await stream.WriteAsync(ServiceStatusFrames.Encode(reply), token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or
            UnauthorizedAccessException or OperationCanceledException)
        {
            // InvalidDataException is not an IOException. Refusal must not escape to SCM.
            return false;
        }
    }
}
