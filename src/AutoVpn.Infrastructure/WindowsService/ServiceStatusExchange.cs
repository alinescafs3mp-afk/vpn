using AutoVpn.Contracts;

namespace AutoVpn.Infrastructure.WindowsService;

/// <summary>One bounded, read-only exchange. Malformed client input does not terminate the listener.</summary>
public static class ServiceStatusExchange
{
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
