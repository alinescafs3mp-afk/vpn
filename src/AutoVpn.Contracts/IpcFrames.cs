using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using AutoVpn.Domain;

namespace AutoVpn.Contracts;

public static class IpcFrames
{
    public static byte[] Encode(object value)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, IpcJson.Options);
        if (json.Length > ProductLimits.MaxIpcFrameBytes)
        {
            throw new InvalidOperationException("IPC frame exceeds the documented bound.");
        }

        var frame = new byte[4 + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, json.Length);
        json.CopyTo(frame.AsSpan(4));
        return frame;
    }

    public static bool TryDecode<T>(ReadOnlySpan<byte> frame, out T? value, out string? error)
    {
        value = default;
        error = null;
        if (frame.Length < 4)
        {
            error = "SHORT_FRAME";
            return false;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(frame);
        if (length <= 0 || length > ProductLimits.MaxIpcFrameBytes || frame.Length != 4 + length)
        {
            error = "FRAME_SIZE";
            return false;
        }

        try
        {
            value = JsonSerializer.Deserialize<T>(frame.Slice(4), IpcJson.RequestOptions);
            if (value is null)
            {
                error = "EMPTY";
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            error = "MALFORMED";
            return false;
        }
    }

    public static string DescribeOversize()
    {
        return $"Кадр IPC больше {ProductLimits.MaxIpcFrameBytes} байт и отклонён.";
    }
}

public static class ForbiddenPayload
{
    private static readonly string[] Names =
    [
        "yaml", "profilePath", "executable", "command", "script", "url", "probeUrl", "firewallRule",
    ];

    public static bool ContainsForbidden(JsonElement payload, out string name)
    {
        name = "";
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in payload.EnumerateObject())
        {
            if (Names.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            {
                name = property.Name;
                return true;
            }
        }

        return false;
    }
}
