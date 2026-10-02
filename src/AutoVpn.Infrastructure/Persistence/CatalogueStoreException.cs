namespace AutoVpn.Infrastructure.Persistence;

public sealed class CatalogueStoreException : InvalidOperationException
{
    public CatalogueStoreException(string message, string? quarantinePath = null)
        : base(message)
    {
        QuarantinePath = quarantinePath;
    }

    public string? QuarantinePath { get; }
}
