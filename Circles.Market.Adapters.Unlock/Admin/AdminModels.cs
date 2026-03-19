using System.Text.Json.Serialization;

namespace Circles.Market.Adapters.Unlock.Admin;

public sealed class UnlockMappingUpsertRequest
{
    [JsonPropertyName("chainId")] public long ChainId { get; set; }
    [JsonPropertyName("seller")] public string Seller { get; set; } = string.Empty;
    [JsonPropertyName("sku")] public string Sku { get; set; } = string.Empty;
    [JsonPropertyName("lockAddress")] public string LockAddress { get; set; } = string.Empty;
    [JsonPropertyName("rpcUrl")] public string RpcUrl { get; set; } = string.Empty;
    [JsonPropertyName("servicePrivateKey")] public string ServicePrivateKey { get; set; } = string.Empty;
    [JsonPropertyName("durationSeconds")] public long? DurationSeconds { get; set; }
    [JsonPropertyName("expirationUnix")] public long? ExpirationUnix { get; set; }
    [JsonPropertyName("keyManagerMode")] public string KeyManagerMode { get; set; } = "buyer";
    [JsonPropertyName("fixedKeyManager")] public string? FixedKeyManager { get; set; }
    [JsonPropertyName("locksmithBase")] public string LocksmithBase { get; set; } = "https://locksmith.unlock-protocol.com";
    [JsonPropertyName("maxSupply")] public long MaxSupply { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
}

public sealed class UnlockMappingDto
{
    [JsonPropertyName("chainId")] public long ChainId { get; set; }
    [JsonPropertyName("seller")] public string Seller { get; set; } = string.Empty;
    [JsonPropertyName("sku")] public string Sku { get; set; } = string.Empty;
    [JsonPropertyName("lockAddress")] public string LockAddress { get; set; } = string.Empty;
    [JsonPropertyName("rpcUrl")] public string RpcUrl { get; set; } = string.Empty;
    [JsonPropertyName("durationSeconds")] public long? DurationSeconds { get; set; }
    [JsonPropertyName("expirationUnix")] public long? ExpirationUnix { get; set; }
    [JsonPropertyName("keyManagerMode")] public string KeyManagerMode { get; set; } = "buyer";
    [JsonPropertyName("fixedKeyManager")] public string? FixedKeyManager { get; set; }
    [JsonPropertyName("locksmithBase")] public string LocksmithBase { get; set; } = "https://locksmith.unlock-protocol.com";
    [JsonPropertyName("hasServicePrivateKey")] public bool HasServicePrivateKey { get; set; }
    [JsonPropertyName("maxSupply")] public long MaxSupply { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("revokedAt")] public DateTimeOffset? RevokedAt { get; set; }
}
