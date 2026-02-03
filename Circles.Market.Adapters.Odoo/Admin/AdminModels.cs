using System.Text.Json.Serialization;

namespace Circles.Market.Adapters.Odoo.Admin;

public sealed class OdooConnectionUpsertRequest
{
    [JsonPropertyName("chainId")] public long ChainId { get; set; }
    [JsonPropertyName("seller")] public string Seller { get; set; } = string.Empty;
    [JsonPropertyName("odooUrl")] public string OdooUrl { get; set; } = string.Empty;
    [JsonPropertyName("odooDb")] public string OdooDb { get; set; } = string.Empty;
    [JsonPropertyName("odooUid")] public int? OdooUid { get; set; }
    [JsonPropertyName("odooKey")] public string OdooKey { get; set; } = string.Empty;
    [JsonPropertyName("salePartnerId")] public int? SalePartnerId { get; set; }
    [JsonPropertyName("jsonrpcTimeoutMs")] public int JsonrpcTimeoutMs { get; set; } = 30000;
    [JsonPropertyName("fulfillInheritRequestAbort")] public bool FulfillInheritRequestAbort { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
}

public sealed class OdooConnectionDto
{
    [JsonPropertyName("chainId")] public long ChainId { get; set; }
    [JsonPropertyName("seller")] public string Seller { get; set; } = string.Empty;
    [JsonPropertyName("odooUrl")] public string OdooUrl { get; set; } = string.Empty;
    [JsonPropertyName("odooDb")] public string OdooDb { get; set; } = string.Empty;
    [JsonPropertyName("odooUid")] public int? OdooUid { get; set; }
    [JsonPropertyName("salePartnerId")] public int? SalePartnerId { get; set; }
    [JsonPropertyName("jsonrpcTimeoutMs")] public int JsonrpcTimeoutMs { get; set; }
    [JsonPropertyName("fulfillInheritRequestAbort")] public bool FulfillInheritRequestAbort { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("revokedAt")] public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class InventoryMappingUpsertRequest
{
    [JsonPropertyName("chainId")] public long ChainId { get; set; }
    [JsonPropertyName("seller")] public string Seller { get; set; } = string.Empty;
    [JsonPropertyName("sku")] public string Sku { get; set; } = string.Empty;
    [JsonPropertyName("odooProductCode")] public string OdooProductCode { get; set; } = string.Empty;
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
}

public sealed class InventoryMappingDto
{
    [JsonPropertyName("chainId")] public long ChainId { get; set; }
    [JsonPropertyName("seller")] public string Seller { get; set; } = string.Empty;
    [JsonPropertyName("sku")] public string Sku { get; set; } = string.Empty;
    [JsonPropertyName("odooProductCode")] public string OdooProductCode { get; set; } = string.Empty;
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("revokedAt")] public DateTimeOffset? RevokedAt { get; set; }
}
