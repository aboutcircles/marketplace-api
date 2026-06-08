using System.Text.Json;
using System.Text.Json.Serialization;

namespace Circles.Market.Api.Admin;

public sealed class AddOdooProductRequest
{
    [JsonPropertyName("chainId")] public long ChainId { get; set; }
    [JsonPropertyName("seller")] public string Seller { get; set; } = string.Empty;
    [JsonPropertyName("sku")] public string Sku { get; set; } = string.Empty;
    [JsonPropertyName("odooProductCode")] public string OdooProductCode { get; set; } = string.Empty;
    [JsonPropertyName("totalInventory")] public long? TotalInventory { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
}

public sealed class AdminOdooConnectionUpsertRequest
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

public sealed class AdminOdooConnectionDto
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

public sealed class AddCodeProductRequest
{
    [JsonPropertyName("chainId")] public long ChainId { get; set; }
    [JsonPropertyName("seller")] public string Seller { get; set; } = string.Empty;
    [JsonPropertyName("sku")] public string Sku { get; set; } = string.Empty;
    [JsonPropertyName("poolId")] public string PoolId { get; set; } = string.Empty;
    [JsonPropertyName("downloadUrlTemplate")] public string? DownloadUrlTemplate { get; set; }
    [JsonPropertyName("codes")] public List<string>? Codes { get; set; }
    [JsonPropertyName("totalInventory")] public long? TotalInventory { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
}

public sealed class AddUnlockProductRequest
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
    [JsonPropertyName("locksmithToken")] public string? LocksmithToken { get; set; }
    [JsonPropertyName("totalInventory")] public long? TotalInventory { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
}

public sealed class AdminRouteDto
{
    [JsonPropertyName("chainId")] public long ChainId { get; set; }
    [JsonPropertyName("seller")] public string Seller { get; set; } = string.Empty;
    [JsonPropertyName("sku")] public string Sku { get; set; } = string.Empty;
    [JsonPropertyName("offerType")] public string? OfferType { get; set; }
    [JsonPropertyName("isOneOff")] public bool IsOneOff { get; set; }
    [JsonPropertyName("totalInventory")] public long? TotalInventory { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
}

public sealed class UpsertRouteRequest
{
    [JsonPropertyName("chainId")] public long ChainId { get; set; }
    [JsonPropertyName("seller")] public string Seller { get; set; } = string.Empty;
    [JsonPropertyName("sku")] public string Sku { get; set; } = string.Empty;
    [JsonPropertyName("offerType")] public string? OfferType { get; set; }
    [JsonPropertyName("isOneOff")] public bool IsOneOff { get; set; }
    [JsonPropertyName("totalInventory")] public long? TotalInventory { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
}

public sealed class AdminOdooProductDto
{
    [JsonPropertyName("chainId")] public long ChainId { get; set; }
    [JsonPropertyName("seller")] public string Seller { get; set; } = string.Empty;
    [JsonPropertyName("sku")] public string Sku { get; set; } = string.Empty;
    [JsonPropertyName("odooProductCode")] public string OdooProductCode { get; set; } = string.Empty;
    [JsonPropertyName("localAvailableQty")] public long? LocalAvailableQty { get; set; }
    [JsonPropertyName("totalInventory")] public long? TotalInventory { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("revokedAt")] public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class AdminOdooStockUpsertRequest
{
    [JsonPropertyName("chainId")] public long ChainId { get; set; }
    [JsonPropertyName("seller")] public string Seller { get; set; } = string.Empty;
    [JsonPropertyName("sku")] public string Sku { get; set; } = string.Empty;
    [JsonPropertyName("availableQty")] public long AvailableQty { get; set; }
}

public sealed class AdminOdooProductVariantDto
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
    [JsonPropertyName("default_code")] public string? DefaultCode { get; set; }
    [JsonPropertyName("product_tmpl_id")] public JsonElement ProductTemplateRaw { get; set; }
    [JsonPropertyName("barcode")] public string? Barcode { get; set; }
    [JsonPropertyName("qty_available")] public decimal QtyAvailable { get; set; }
    [JsonPropertyName("active")] public bool Active { get; set; }
}

public sealed class AdminOdooProductVariantQueryResult
{
    [JsonPropertyName("items")] public List<AdminOdooProductVariantDto> Items { get; set; } = new();
    [JsonPropertyName("limit")] public int Limit { get; set; }
    [JsonPropertyName("offset")] public int Offset { get; set; }
    [JsonPropertyName("activeOnly")] public bool ActiveOnly { get; set; }
    [JsonPropertyName("hasCode")] public bool HasCode { get; set; }
}

public sealed class AdminCodeProductDto
{
    [JsonPropertyName("chainId")] public long ChainId { get; set; }
    [JsonPropertyName("seller")] public string Seller { get; set; } = string.Empty;
    [JsonPropertyName("sku")] public string Sku { get; set; } = string.Empty;
    [JsonPropertyName("poolId")] public string PoolId { get; set; } = string.Empty;
    [JsonPropertyName("downloadUrlTemplate")] public string? DownloadUrlTemplate { get; set; }
    [JsonPropertyName("totalInventory")] public long? TotalInventory { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("revokedAt")] public DateTimeOffset? RevokedAt { get; set; }
    [JsonPropertyName("poolRemaining")] public long? PoolRemaining { get; set; }
}

public sealed class AdminUnlockProductDto
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
    [JsonPropertyName("maxSupply")] public long MaxSupply { get; set; }
    [JsonPropertyName("totalInventory")] public long? TotalInventory { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("revokedAt")] public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class AdminCodePoolDto
{
    [JsonPropertyName("poolId")] public string PoolId { get; set; } = string.Empty;
    [JsonPropertyName("remaining")] public long Remaining { get; set; }
}

// ── WooCommerce ──────────────────────────────────────────────────────────────

public sealed class AdminWcConnectionUpsertRequest
{
    [JsonPropertyName("chainId")] public long ChainId { get; set; }
    [JsonPropertyName("seller")] public string Seller { get; set; } = string.Empty;
    [JsonPropertyName("wcBaseUrl")] public string WcBaseUrl { get; set; } = string.Empty;
    [JsonPropertyName("wcConsumerKey")] public string WcConsumerKey { get; set; } = string.Empty;
    [JsonPropertyName("wcConsumerSecret")] public string WcConsumerSecret { get; set; } = string.Empty;
    [JsonPropertyName("defaultCustomerId")] public int? DefaultCustomerId { get; set; }
    [JsonPropertyName("orderStatus")] public string OrderStatus { get; set; } = "pending";
    [JsonPropertyName("timeoutMs")] public int TimeoutMs { get; set; } = 30000;
    [JsonPropertyName("fulfillInheritRequestAbort")] public bool FulfillInheritRequestAbort { get; set; } = true;
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
}

public sealed class AdminWcConnectionDto
{
    [JsonPropertyName("chainId")] public long ChainId { get; set; }
    [JsonPropertyName("seller")] public string Seller { get; set; } = string.Empty;
    [JsonPropertyName("wcBaseUrl")] public string WcBaseUrl { get; set; } = string.Empty;
    [JsonPropertyName("wcConsumerKey")] public string WcConsumerKey { get; set; } = string.Empty;
    [JsonPropertyName("defaultCustomerId")] public int? DefaultCustomerId { get; set; }
    [JsonPropertyName("orderStatus")] public string OrderStatus { get; set; } = string.Empty;
    [JsonPropertyName("timeoutMs")] public int TimeoutMs { get; set; }
    [JsonPropertyName("fulfillInheritRequestAbort")] public bool FulfillInheritRequestAbort { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("revokedAt")] public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class AddWcProductRequest
{
    [JsonPropertyName("chainId")] public long ChainId { get; set; }
    [JsonPropertyName("seller")] public string Seller { get; set; } = string.Empty;
    [JsonPropertyName("sku")] public string Sku { get; set; } = string.Empty;
    [JsonPropertyName("wcProductSku")] public string WcProductSku { get; set; } = string.Empty;
    [JsonPropertyName("wcProductId")] public int? WcProductId { get; set; }
    [JsonPropertyName("totalInventory")] public long? TotalInventory { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
}

public sealed class AdminWcProductDto
{
    [JsonPropertyName("chainId")] public long ChainId { get; set; }
    [JsonPropertyName("seller")] public string Seller { get; set; } = string.Empty;
    [JsonPropertyName("sku")] public string Sku { get; set; } = string.Empty;
    [JsonPropertyName("wcProductSku")] public string WcProductSku { get; set; } = string.Empty;
    [JsonPropertyName("wcProductId")] public int? WcProductId { get; set; }
    [JsonPropertyName("totalInventory")] public long? TotalInventory { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("revokedAt")] public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class AdminWcStockUpsertRequest
{
    [JsonPropertyName("chainId")] public long ChainId { get; set; }
    [JsonPropertyName("seller")] public string Seller { get; set; } = string.Empty;
    [JsonPropertyName("sku")] public string Sku { get; set; } = string.Empty;
    [JsonPropertyName("stockQuantity")] public int StockQuantity { get; set; }
}
