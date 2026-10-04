using System.Buffers.Binary;
using System.Text.Json;

namespace AutoVpn.Contracts;

/// <summary>Bounded, strict frames for the installed service. No polymorphic payloads.</summary>
public static class ServiceStatusFrames
{
    public static byte[] Encode<T>(T value)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value, IpcJson.Options);
        if (body.Length is <= 0 or > InstalledServiceProtocol.MaxFrameBytes) throw new InvalidDataException("FRAME_SIZE");
        var result = new byte[body.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(result, body.Length);
        body.CopyTo(result.AsSpan(4));
        return result;
    }

    public static T Decode<T>(ReadOnlyMemory<byte> body)
    {
        if (body.Length is <= 0 or > InstalledServiceProtocol.MaxFrameBytes) throw new InvalidDataException("FRAME_SIZE");
        try
        {
            using var doc = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("OBJECT_REQUIRED");
            RejectDuplicates(doc.RootElement);
            return JsonSerializer.Deserialize<T>(body.Span, IpcJson.RequestOptions) ?? throw new InvalidDataException("EMPTY");
        }
        catch (JsonException ex) { throw new InvalidDataException("MALFORMED", ex); }
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is <= 0 or > InstalledServiceProtocol.MaxFrameBytes) throw new InvalidDataException("FRAME_SIZE");
        var body = new byte[size];
        await stream.ReadExactlyAsync(body, token).ConfigureAwait(false);
        return Decode<T>(body);
    }

    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("DUPLICATE_FIELD");
                RejectDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) RejectDuplicates(child);
    }
}
