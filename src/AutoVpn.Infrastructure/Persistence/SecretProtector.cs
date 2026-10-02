using System.Security.Cryptography;

namespace AutoVpn.Infrastructure.Persistence;

public interface ISecretProtector
{
    string ProtectorId { get; }

    byte[] Protect(ReadOnlySpan<byte> plaintext);

    byte[] Unprotect(ReadOnlySpan<byte> ciphertext);
}

public static class SecretProtectors
{
    public static ISecretProtector ForProductionHost()
    {
        return OperatingSystem.IsWindows()
            ? new DpapiSecretProtector()
            : new UnavailableSecretProtector();
    }
}

/// <summary>
/// Windows DPAPI, current-user scope. This is not available on the Linux build host.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "AutoVPN.catalogue.v1"u8.ToArray();

    public string ProtectorId => "dpapi-current-user";

    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI is only available on Windows.");
        }

        return ProtectedData.Protect(plaintext.ToArray(), Entropy, DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(ReadOnlySpan<byte> ciphertext)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI is only available on Windows.");
        }

        return ProtectedData.Unprotect(ciphertext.ToArray(), Entropy, DataProtectionScope.CurrentUser);
    }
}

/// <summary>
/// Explicit test double. Bytes are not encrypted. Never the production default.
/// </summary>
public sealed class PassthroughSecretProtector : ISecretProtector
{
    public string ProtectorId => "test-passthrough";

    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        return plaintext.ToArray();
    }

    public byte[] Unprotect(ReadOnlySpan<byte> ciphertext)
    {
        return ciphertext.ToArray();
    }
}

public sealed class UnavailableSecretProtector : ISecretProtector
{
    public string ProtectorId => "unavailable";

    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        throw new InvalidOperationException("Refusing to persist node secrets without Windows DPAPI.");
    }

    public byte[] Unprotect(ReadOnlySpan<byte> ciphertext)
    {
        throw new InvalidOperationException("Refusing to read node secrets without Windows DPAPI.");
    }
}
