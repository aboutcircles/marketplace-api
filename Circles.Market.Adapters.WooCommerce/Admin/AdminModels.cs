using System.Text.Json.Serialization;

namespace Circles.Market.Adapters.WooCommerce.Admin;

// ── Connection ───────────────────────────────────────────────────────────────

public sealed class WooCommerceConnectionDto
{
    [JsonPropertyName("chainId")] public long ChainId { get; init; }
    [JsonPropertyName("seller")] public string Seller { get; init; } = string.Empty;
    [JsonPropertyName("wcBaseUrl")] public string WcBaseUrl { get; init; } = string.Empty;
    [JsonPropertyName("wcConsumerKey")] public string WcConsumerKey { get; init; } = string.Empty;
    [JsonPropertyName("defaultCustomerId")] public int? DefaultCustomerId { get; init; }
    [JsonPropertyName("orderStatus")] public string OrderStatus { get; init; } = string.Empty;
    [JsonPropertyName("timeoutMs")] public int TimeoutMs { get; init; }
    [JsonPropertyName("fulfillInheritRequestAbort")] public bool FulfillInheritRequestAbort { get; init; }
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("revokedAt")] public DateTimeOffset? RevokedAt { get; init; }
}

public sealed class WooCommerceConnectionUpsertRequest
{
    [JsonPropertyName("chainId")] public long ChainId { get; init; }
    [JsonPropertyName("seller")] public string Seller { get; init; } = string.Empty;
    [JsonPropertyName("wcBaseUrl")] public string WcBaseUrl { get; init; } = string.Empty;
    [JsonPropertyName("wcConsumerKey")] public string WcConsumerKey { get; init; } = string.Empty;
    [JsonPropertyName("wcConsumerSecret")] public string WcConsumerSecret { get; init; } = string.Empty;
    [JsonPropertyName("defaultCustomerId")] public int? DefaultCustomerId { get; init; }
    [JsonPropertyName("orderStatus")] public string OrderStatus { get; init; } = "pending";
    [JsonPropertyName("timeoutMs")] public int TimeoutMs { get; init; } = 30000;
    [JsonPropertyName("fulfillInheritRequestAbort")] public bool FulfillInheritRequestAbort { get; init; } = true;
    [JsonPropertyName("enabled")] public bool Enabled { get; init; } = true;
}

// ── Product Mapping ──────────────────────────────────────────────────────────

public sealed class ProductMappingDto
{
    [JsonPropertyName("chainId")] public long ChainId { get; init; }
    [JsonPropertyName("seller")] public string Seller { get; init; } = string.Empty;
    [JsonPropertyName("sku")] public string Sku { get; init; } = string.Empty;
    [JsonPropertyName("wcProductSku")] public string WcProductSku { get; init; } = string.Empty;
    [JsonPropertyName("wcProductId")] public int? WcProductId { get; init; }
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("revokedAt")] public DateTimeOffset? RevokedAt { get; init; }
}

public sealed class ProductMappingUpsertRequest
{
    [JsonPropertyName("chainId")] public long ChainId { get; init; }
    [JsonPropertyName("seller")] public string Seller { get; init; } = string.Empty;
    [JsonPropertyName("sku")] public string Sku { get; init; } = string.Empty;
    [JsonPropertyName("wcProductSku")] public string WcProductSku { get; init; } = string.Empty;
    [JsonPropertyName("wcProductId")] public int? WcProductId { get; init; }
    [JsonPropertyName("enabled")] public bool Enabled { get; init; } = true;
}

// ── Inventory Stock ──────────────────────────────────────────────────────────

public sealed class StockUpsertRequest
{
    [JsonPropertyName("chainId")] public long ChainId { get; init; }
    [JsonPropertyName("seller")] public string Seller { get; init; } = string.Empty;
    [JsonPropertyName("sku")] public string Sku { get; init; } = string.Empty;
    [JsonPropertyName("stockQuantity")] public int StockQuantity { get; init; } // -1 = unlimited
}

// ── Fulfillment Run ─────────────────────────────────────────────────────────

public sealed class FulfillmentRunDto
{
    [JsonPropertyName("id")] public Guid Id { get; init; }
    [JsonPropertyName("chainId")] public long ChainId { get; init; }
    [JsonPropertyName("seller")] public string Seller { get; init; } = string.Empty;
    [JsonPropertyName("paymentReference")] public string PaymentReference { get; init; } = string.Empty;
    [JsonPropertyName("idempotencyKey")] public Guid IdempotencyKey { get; init; }
    [JsonPropertyName("wcOrderId")] public int? WcOrderId { get; init; }
    [JsonPropertyName("wcOrderNumber")] public string? WcOrderNumber { get; init; }
    [JsonPropertyName("status")] public string Status { get; init; } = string.Empty;
    [JsonPropertyName("outcome")] public string? Outcome { get; init; }
    [JsonPropertyName("errorDetail")] public string? ErrorDetail { get; init; }
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("completedAt")] public DateTimeOffset? CompletedAt { get; init; }
}