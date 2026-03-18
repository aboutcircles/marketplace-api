namespace Circles.Market.Adapters.Unlock;

public sealed class UnlockMappingEntry
{
    public long ChainId { get; init; }
    public string Seller { get; init; } = string.Empty;
    public string Sku { get; init; } = string.Empty;
    public string LockAddress { get; init; } = string.Empty;
    public string RpcUrl { get; init; } = string.Empty;
    public string ServicePrivateKey { get; init; } = string.Empty;
    public long? DurationSeconds { get; init; }
    public long? ExpirationUnix { get; init; }
    public string KeyManagerMode { get; init; } = "buyer"; // buyer | service | fixed
    public string? FixedKeyManager { get; init; }
    public string LocksmithBase { get; init; } = "https://locksmith.unlock-protocol.com";
    public string? LocksmithToken { get; init; }
    public long MaxSupply { get; init; }
    public bool Enabled { get; init; } = true;
    public DateTimeOffset? RevokedAt { get; init; }
}

public interface IUnlockMappingResolver
{
    Task<(bool Mapped, UnlockMappingEntry? Entry)> TryResolveAsync(long chainId, string sellerAddress, string sku, CancellationToken ct);
    Task<List<UnlockMappingEntry>> ListMappingsAsync(CancellationToken ct);
    Task UpsertMappingAsync(UnlockMappingEntry entry, bool enabled, CancellationToken ct);
    Task<bool> DisableMappingAsync(long chainId, string sellerAddress, string sku, CancellationToken ct);
}
