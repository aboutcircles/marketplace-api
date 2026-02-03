using System.Text.Json.Serialization;

namespace Circles.Market.Api.Admin;

public sealed class AddOdooProductRequest
{
    [JsonPropertyName("chainId")] public long ChainId { get; set; }
    [JsonPropertyName("seller")] public string Seller { get; set; } = string.Empty;
    [JsonPropertyName("sku")] public string Sku { get; set; } = string.Empty;
    [JsonPropertyName("odooProductCode")] public string OdooProductCode { get; set; } = string.Empty;
    [JsonPropertyName("odooUrl")] public string OdooUrl { get; set; } = string.Empty;
    [JsonPropertyName("odooDb")] public string OdooDb { get; set; } = string.Empty;
    [JsonPropertyName("odooUid")] public int? OdooUid { get; set; }
    [JsonPropertyName("odooKey")] public string OdooKey { get; set; } = string.Empty;
    [JsonPropertyName("salePartnerId")] public int? SalePartnerId { get; set; }
    [JsonPropertyName("jsonrpcTimeoutMs")] public int JsonrpcTimeoutMs { get; set; } = 30000;
    [JsonPropertyName("fulfillInheritRequestAbort")] public bool FulfillInheritRequestAbort { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
}

public sealed class AddCodeProductRequest
{
    [JsonPropertyName("chainId")] public long ChainId { get; set; }
    [JsonPropertyName("seller")] public string Seller { get; set; } = string.Empty;
    [JsonPropertyName("sku")] public string Sku { get; set; } = string.Empty;
    [JsonPropertyName("poolId")] public string PoolId { get; set; } = string.Empty;
    [JsonPropertyName("downloadUrlTemplate")] public string? DownloadUrlTemplate { get; set; }
    [JsonPropertyName("codes")] public List<string>? Codes { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
}

public sealed class AdminRouteDto
{
    [JsonPropertyName("chainId")] public long ChainId { get; set; }
    [JsonPropertyName("seller")] public string Seller { get; set; } = string.Empty;
    [JsonPropertyName("sku")] public string Sku { get; set; } = string.Empty;
    [JsonPropertyName("offerType")] public string? OfferType { get; set; }
    [JsonPropertyName("isOneOff")] public bool IsOneOff { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
}

public sealed class UpsertRouteRequest
{
    [JsonPropertyName("chainId")] public long ChainId { get; set; }
    [JsonPropertyName("seller")] public string Seller { get; set; } = string.Empty;
    [JsonPropertyName("sku")] public string Sku { get; set; } = string.Empty;
    [JsonPropertyName("offerType")] public string? OfferType { get; set; }
    [JsonPropertyName("isOneOff")] public bool IsOneOff { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
}

public sealed class AdminOdooProductDto
{
    [JsonPropertyName("chainId")] public long ChainId { get; set; }
    [JsonPropertyName("seller")] public string Seller { get; set; } = string.Empty;
    [JsonPropertyName("sku")] public string Sku { get; set; } = string.Empty;
    [JsonPropertyName("odooProductCode")] public string OdooProductCode { get; set; } = string.Empty;
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("revokedAt")] public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class AdminCodeProductDto
{
    [JsonPropertyName("chainId")] public long ChainId { get; set; }
    [JsonPropertyName("seller")] public string Seller { get; set; } = string.Empty;
    [JsonPropertyName("sku")] public string Sku { get; set; } = string.Empty;
    [JsonPropertyName("poolId")] public string PoolId { get; set; } = string.Empty;
    [JsonPropertyName("downloadUrlTemplate")] public string? DownloadUrlTemplate { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("revokedAt")] public DateTimeOffset? RevokedAt { get; set; }
    [JsonPropertyName("poolRemaining")] public long? PoolRemaining { get; set; }
}

public sealed class AdminCodePoolDto
{
    [JsonPropertyName("poolId")] public string PoolId { get; set; } = string.Empty;
    [JsonPropertyName("remaining")] public long Remaining { get; set; }
}
