using System.Globalization;

namespace AutoVpn.Contracts;

public sealed record ServiceOwnerConfiguration
{
    public required int SchemaVersion { get; init; }
    public required string OwnerSid { get; init; }

    public static ServiceOwnerConfiguration Parse(ReadOnlyMemory<byte> body)
    {
        var config = ServiceStatusFrames.Decode<ServiceOwnerConfiguration>(body);
        if (config.SchemaVersion != 1 || !IsAccountSid(config.OwnerSid))
            throw new InvalidDataException("SERVICE_OWNER_INVALID");
        return config;
    }

    // Deliberately supports one local/domain account only, not broad groups or service identities.
    public static bool IsAccountSid(string? sid)
    {
        if (sid is null || sid.Length > 96) return false;
        var parts = sid.Split('-');
        if (parts.Length != 8 || parts[0] != "S" || parts[1] != "1" || parts[2] != "5" || parts[3] != "21") return false;
        for (var i = 4; i < parts.Length; i++)
            if (!uint.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
                parts[i] != value.ToString(CultureInfo.InvariantCulture) || (i == 7 && value == 0)) return false;
        return true;
    }
}
