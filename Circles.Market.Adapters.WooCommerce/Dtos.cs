using System.Text.Json;
using System.Text.Json.Serialization;

namespace Circles.Market.Adapters.WooCommerce;

// ─────────────────────────────────────────────────────────────────────────────
// WooCommerce REST API v3 DTOs
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Minimal WooCommerce product returned by GET /products.</summary>
public class WcProductDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("sku")]
    public string? Sku { get; set; }

    [JsonPropertyName("slug")]
    public string? Slug { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("price")]
    public string? Price { get; set; }

    [JsonPropertyName("regular_price")]
    public string? RegularPrice { get; set; }

    [JsonPropertyName("sale_price")]
    public string? SalePrice { get; set; }

    [JsonPropertyName("on_sale")]
    public bool OnSale { get; set; }

    [JsonPropertyName("stock_quantity")]
    public int? StockQuantity { get; set; }

    [JsonPropertyName("stock_status")]
    public string? StockStatus { get; set; } // "instock", "outofstock", "onbackorder"

    [JsonPropertyName("manage_stock")]
    public bool ManageStock { get; set; }

    [JsonPropertyName("purchasable")]
    public bool Purchasable { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; } // "simple", "variable", "grouped", "external"

    [JsonPropertyName("permalink")]
    public string? Permalink { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("categories")]
    public List<WcProductCategoryDto>? Categories { get; set; }
}

public class WcProductCategoryDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("slug")]
    public string? Slug { get; set; }
}

/// <summary>WooCommerce order line item payload.</summary>
public class WcLineItemDto
{
    [JsonPropertyName("product_id")]
    public int ProductId { get; set; }

    [JsonPropertyName("quantity")]
    public int Quantity { get; set; }

    /// <summary>Override price per unit (optional — WC uses product price if omitted).</summary>
    [JsonPropertyName("price")]
    public string? Price { get; set; }
}

/// <summary>Address block used for both billing and shipping in WooCommerce orders.</summary>
public class WcAddressDto
{
    [JsonPropertyName("first_name")]
    public string? FirstName { get; set; }

    [JsonPropertyName("last_name")]
    public string? LastName { get; set; }

    [JsonPropertyName("address_1")]
    public string? Address1 { get; set; }

    [JsonPropertyName("address_2")]
    public string? Address2 { get; set; }

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("postcode")]
    public string? Postcode { get; set; }

    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("phone")]
    public string? Phone { get; set; }
}

/// <summary>Payload for POST /orders.</summary>
public class WcCreateOrderRequest
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "pending";

    [JsonPropertyName("customer_id")]
    public int? CustomerId { get; set; }

    [JsonPropertyName("billing")]
    public WcAddressDto? Billing { get; set; }

    [JsonPropertyName("shipping")]
    public WcAddressDto? Shipping { get; set; }

    [JsonPropertyName("line_items")]
    public List<WcLineItem> LineItems { get; set; } = new();

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "EUR";

    /// <summary>Free-text order note (e.g. payment reference).</summary>
    [JsonPropertyName("customer_note")]
    public bool CustomerNote { get; set; }

    /// <summary>Metadata key-value pairs, e.g. "payment_reference".</summary>
    [JsonPropertyName("meta_data")]
    public List<WcMetaData>? MetaData { get; set; }
}

public class WcMetaDataItem
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;
}

/// <summary>Response from POST /orders or GET /orders/{id}.</summary>
public class WcOrderDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("number")]
    public string Number { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("billing")]
    public WcAddressDto? Billing { get; set; }

    [JsonPropertyName("shipping")]
    public WcAddressDto? Shipping { get; set; }

    [JsonPropertyName("line_items")]
    public List<WcLineItemDto>? LineItems { get; set; }

    [JsonPropertyName("total")]
    public string? Total { get; set; }

    [JsonPropertyName("date_created")]
    public DateTimeOffset? DateCreated { get; set; }

    [JsonPropertyName("date_modified")]
    public DateTimeOffset? DateModified { get; set; }
}

/// <summary>WooCommerce customer response from GET /customers.</summary>
public class WcCustomerDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("first_name")]
    public string? FirstName { get; set; }

    [JsonPropertyName("last_name")]
    public string? LastName { get; set; }

    [JsonPropertyName("billing")]
    public WcAddressDto? Billing { get; set; }

    [JsonPropertyName("shipping")]
    public WcAddressDto? Shipping { get; set; }
}

/// <summary>WooCommerce API error envelope.</summary>
public class WcApiErrorDto
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }
}

// ─────────────────────────────────────────────────────────────────────────────
// Internal adapter DTOs
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Inventory result returned by GET /inventory.</summary>
public class WooCommerceInventoryResult
{
    [JsonPropertyName("sku")]
    public string Sku { get; set; } = string.Empty;

    [JsonPropertyName("wcProductId")]
    public int WcProductId { get; set; }

    [JsonPropertyName("wcProductSku")]
    public string? WcProductSku { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("stockQuantity")]
    public int StockQuantity { get; set; }

    [JsonPropertyName("inStock")]
    public bool InStock { get; set; }

    [JsonPropertyName("stockStatus")]
    public string? StockStatus { get; set; }

    [JsonPropertyName("price")]
    public string? Price { get; set; }

    [JsonPropertyName("regularPrice")]
    public string? RegularPrice { get; set; }

    [JsonPropertyName("salePrice")]
    public string? SalePrice { get; set; }

    [JsonPropertyName("onSale")]
    public bool OnSale { get; set; }

    [JsonPropertyName("manageStock")]
    public bool ManageStock { get; set; }

    /// <summary>"local" if from wc_inventory_stock override, "wc" if from WooCommerce API.</summary>
    [JsonPropertyName("stockSource")]
    public string StockSource { get; set; } = "wc";
}

/// <summary>Availability result returned by GET /availability.</summary>
public class WooCommerceAvailabilityResult
{
    [JsonPropertyName("sku")]
    public string Sku { get; set; } = string.Empty;

    [JsonPropertyName("available")]
    public bool Available { get; set; }

    [JsonPropertyName("stockQuantity")]
    public int? StockQuantity { get; set; }
}

/// <summary>
/// Fulfillment outcome for POST /fulfill.
/// Mirrors the Circles fulfillment result shape used by other adapters.
/// </summary>
public class WooCommerceFulfillmentResult
{
    /// <summary>"success" | "already_fulfilled" | "validation_error" | "wc_api_error"</summary>
    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = string.Empty;

    [JsonPropertyName("wcOrderId")]
    public int? WcOrderId { get; set; }

    [JsonPropertyName("wcOrderNumber")]
    public string? WcOrderNumber { get; set; }

    [JsonPropertyName("wcOrderStatus")]
    public string? WcOrderStatus { get; set; }

    [JsonPropertyName("paymentReference")]
    public string? PaymentReference { get; set; }

    [JsonPropertyName("fulfillmentRunId")]
    public Guid? FulfillmentRunId { get; set; }

    [JsonPropertyName("createdAt")]
    public string? CreatedAt { get; set; }

    /// <summary>Populated when outcome is "validation_error".</summary>
    [JsonPropertyName("errors")]
    public List<string>? Errors { get; set; }

    /// <summary>Populated when outcome is "wc_api_error".</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>Populated when outcome is "wc_api_error".</summary>
    [JsonPropertyName("wcErrorCode")]
    public string? WcErrorCode { get; set; }
}

// ─────────────────────────────────────────────────────────────────────────────
// FulfillmentRun database record (mirrors the shared library shape)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Minimal record written to <c>wc_fulfillment_runs</c> for idempotency.</summary>
public sealed class WooCommerceFulfillmentRunRecord
{
    public Guid Id { get; set; }
    public long ChainId { get; set; }
    public string SellerAddress { get; set; } = string.Empty;
    public string PaymentReference { get; set; } = string.Empty;
    public Guid IdempotencyKey { get; set; }
    public int? WcOrderId { get; set; }
    public string? WcOrderNumber { get; set; }
    public string Status { get; set; } = "pending"; // pending | completed | failed
    public string? Outcome { get; set; }            // success | already_fulfilled | validation_error | wc_api_error
    public string? ErrorDetail { get; set; }
    public string RequestPayload { get; set; } = string.Empty;
    public string? ResponsePayload { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>
/// Customer record stored in <c>wc_customers</c>.
/// </summary>
public sealed class WooCommerceCustomerRecord
{
    public Guid Id { get; set; }
    public long ChainId { get; set; }
    public string SellerAddress { get; set; } = string.Empty;
    public string BuyerAddress { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? GivenName { get; set; }
    public string? FamilyName { get; set; }
    public string? Telephone { get; set; }
    public string? StreetAddress { get; set; }
    public string? AddressLocality { get; set; }
    public string? PostalCode { get; set; }
    public string? AddressCountry { get; set; }
    /// <summary>Set if a WC customer was pre-created (Phase 2 webhooks).</summary>
    public int? WcCustomerId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Local stock override stored in <c>wc_inventory_stock</c>.
/// </summary>
public sealed class WooCommerceStockOverride
{
    public Guid Id { get; set; }
    public long ChainId { get; set; }
    public string SellerAddress { get; set; } = string.Empty;
    public string Sku { get; set; } = string.Empty;
    /// <summary>-1 means unlimited (treat as always in stock).</summary>
    public int StockQuantity { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

// ─────────────────────────────────────────────────────────────────────────────
// Fulfillment request DTOs (deserialized from the API gateway)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Incoming fulfillment request body for POST /fulfill.</summary>
public sealed class WooCommerceFulfillmentRequest
{
    [JsonPropertyName("orderId")]
    public string? OrderId { get; set; }

    [JsonPropertyName("paymentReference")]
    public string? PaymentReference { get; set; }

    [JsonPropertyName("idempotencyKey")]
    public Guid? IdempotencyKey { get; set; }

    [JsonPropertyName("buyer")]
    public string? Buyer { get; set; }

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }

    [JsonPropertyName("contactPoint")]
    public WooCommerceFulfillmentContact? ContactPoint { get; set; }

    [JsonPropertyName("customer")]
    public WooCommerceFulfillmentCustomer? Customer { get; set; }

    [JsonPropertyName("billingAddress")]
    public WooCommerceFulfillmentAddress? BillingAddress { get; set; }

    [JsonPropertyName("shippingAddress")]
    public WooCommerceFulfillmentAddress? ShippingAddress { get; set; }

    [JsonPropertyName("items")]
    public List<WooCommerceFulfillmentItem>? Items { get; set; }
}

public sealed class WooCommerceFulfillmentContact
{
    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("telephone")]
    public string? Telephone { get; set; }
}

public sealed class WooCommerceFulfillmentCustomer
{
    [JsonPropertyName("givenName")]
    public string? GivenName { get; set; }

    [JsonPropertyName("familyName")]
    public string? FamilyName { get; set; }
}

public sealed class WooCommerceFulfillmentAddress
{
    [JsonPropertyName("givenName")]
    public string? GivenName { get; set; }

    [JsonPropertyName("familyName")]
    public string? FamilyName { get; set; }

    [JsonPropertyName("streetAddress")]
    public string? StreetAddress { get; set; }

    [JsonPropertyName("addressLocality")]
    public string? AddressLocality { get; set; }

    [JsonPropertyName("postalCode")]
    public string? PostalCode { get; set; }

    [JsonPropertyName("addressCountry")]
    public string? AddressCountry { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("telephone")]
    public string? Telephone { get; set; }
}

public sealed class WooCommerceFulfillmentItem
{
    [JsonPropertyName("sku")]
    public string Sku { get; set; } = string.Empty;

    [JsonPropertyName("quantity")]
    public double Quantity { get; set; }
}

// ─────────────────────────────────────────────────────────────────────────────
// Aliases used in Program.cs (lightweight wrappers over DTO types)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Line item wrapper used when building WC order from fulfillment request.</summary>
public sealed class WcLineItem
{
    [JsonPropertyName("product_id")]
    public int? ProductId { get; set; }

    [JsonPropertyName("sku")]
    public string? Sku { get; set; }

    [JsonPropertyName("quantity")]
    public int Quantity { get; set; }
}

/// <summary>Metadata alias used in order creation.</summary>
public sealed class WcMetaData
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;
}
