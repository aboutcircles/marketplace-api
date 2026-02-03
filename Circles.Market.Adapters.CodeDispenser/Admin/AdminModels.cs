using System.Text.Json.Serialization;

namespace Circles.Market.Adapters.CodeDispenser.Admin;

public sealed class CodePoolCreateRequest
{
    [JsonPropertyName("poolId")] public string PoolId { get; set; } = string.Empty;
}

public sealed class CodePoolSeedRequest
{
    [JsonPropertyName("codes")] public List<string> Codes { get; set; } = new();
}

public sealed class CodePoolEntry
{
    [JsonPropertyName("poolId")] public string PoolId { get; set; } = string.Empty;
    [JsonPropertyName("remaining")] public long Remaining { get; set; }
}

public sealed class CodeMappingUpsertRequest
{
    [JsonPropertyName("chainId")] public long ChainId { get; set; }
    [JsonPropertyName("seller")] public string Seller { get; set; } = string.Empty;
    [JsonPropertyName("sku")] public string Sku { get; set; } = string.Empty;
    [JsonPropertyName("poolId")] public string PoolId { get; set; } = string.Empty;
    [JsonPropertyName("downloadUrlTemplate")] public string? DownloadUrlTemplate { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
}

public sealed class CodeMappingDto
{
    [JsonPropertyName("chainId")] public long ChainId { get; set; }
    [JsonPropertyName("seller")] public string Seller { get; set; } = string.Empty;
    [JsonPropertyName("sku")] public string Sku { get; set; } = string.Empty;
    [JsonPropertyName("poolId")] public string PoolId { get; set; } = string.Empty;
    [JsonPropertyName("downloadUrlTemplate")] public string? DownloadUrlTemplate { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("revokedAt")] public DateTimeOffset? RevokedAt { get; set; }
}
