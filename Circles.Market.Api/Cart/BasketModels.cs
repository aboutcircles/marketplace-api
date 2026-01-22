using System.Text.Json.Serialization;
using Circles.Profiles.Models;

namespace Circles.Market.Api.Cart;

/// <summary>
/// Shared JSON-LD constants used by cart-related payloads (Basket and friends).
/// </summary>
public static class CartJsonLd
{
    public static readonly string[] BasketContext =
    {
        "https://schema.org/",
        "https://aboutcircles.com/contexts/circles-market/"
    };
}

/// <summary>
/// High-level lifecycle of a basket within the cart service.
/// </summary>
public enum BasketStatus
{
    Draft,
    Validating,
    Valid,
    CheckedOut,
    Expired
}

/// <summary>
/// Minimal snapshot of a seller <see href="https://schema.org/Offer">Offer</see> captured at validation time.
/// </summary>
public class OfferSnapshot
{
    [JsonPropertyName("@type")] public string Type { get; set; } = "Offer";
    [JsonPropertyName("price")] public decimal? Price { get; set; }
    [JsonPropertyName("priceCurrency")] public string? PriceCurrency { get; set; }
    [JsonPropertyName("seller")] public SchemaOrgOrgId? Seller { get; set; }
    // Accept either a single string or an array of strings, but always expose List<string>.
    [JsonPropertyName("availableDeliveryMethod")]
    [JsonConverter(typeof(StringOrStringArrayJsonConverter))]
    public List<string>? AvailableDeliveryMethod { get; set; }

    // NEW: offer-driven validation hints, copied from SchemaOrgOffer.requiredSlots
    [JsonPropertyName("requiredSlots")]
    public List<string>? RequiredSlots { get; set; }

    // Fulfillment hints propagated from SchemaOrgOffer
    [JsonPropertyName("fulfillmentEndpoint")]
    public string? CirclesFulfillmentEndpoint { get; set; }

    [JsonPropertyName("fulfillmentTrigger")]
    public string? CirclesFulfillmentTrigger { get; set; }

    // Indicates if this is a one-off offer (no availabilityFeed and no inventoryFeed)
    [JsonPropertyName("isOneOff")]
    public bool? IsOneOff { get; set; }
}

/// <summary>
/// Schema.org Organization identifier wrapper using JSON-LD <c>@id</c>.
/// Example: <c>eip155:100:0xabc…</c>
/// </summary>
public class SchemaOrgOrgId
{
    [JsonPropertyName("@type")] public string Type { get; set; } = "Organization";
    [JsonPropertyName("@id")] public string? Id { get; set; }
}

/// <summary>
/// Basket line preview using <see href="https://schema.org/OrderItem">OrderItem</see> semantics prior to checkout.
/// </summary>
public class OrderItemPreview
{
    [JsonPropertyName("@type")] public string Type { get; set; } = "OrderItem";
    [JsonPropertyName("orderQuantity")] public int OrderQuantity { get; set; }
    [JsonPropertyName("orderedItem")] public OrderedItemRef OrderedItem { get; set; } = new();
    [JsonPropertyName("seller")] public string? Seller { get; set; }
    // Optional image URL for client basket previews (not used in validation)
    // Using a simple URL keeps payload small; aligns with circles-common imageUrl -> schema:image
    [JsonPropertyName("imageUrl")] public string? ImageUrl { get; set; }
    [JsonPropertyName("productCid")] public string? ProductCid { get; set; }
    [JsonPropertyName("offerSnapshot")] public OfferSnapshot? OfferSnapshot { get; set; }
}

/// <summary>
/// Minimal reference to the product being ordered (typically keyed by SKU).
/// </summary>
public class OrderedItemRef
{
    [JsonPropertyName("@type")] public string Type { get; set; } = "Product";
    [JsonPropertyName("sku")] public string? Sku { get; set; }
}

/// <summary>
/// Postal address per <see href="https://schema.org/PostalAddress">schema.org</see>.
/// </summary>
public class PostalAddress
{
    [JsonPropertyName("@type")] public string Type { get; set; } = "PostalAddress";
    [JsonPropertyName("streetAddress")] public string? StreetAddress { get; set; }
    [JsonPropertyName("addressLocality")] public string? AddressLocality { get; set; }
    [JsonPropertyName("postalCode")] public string? PostalCode { get; set; }
    [JsonPropertyName("addressCountry")] public string? AddressCountry { get; set; }
}

/// <summary>
/// Minimal person shape for age/identity checks.
/// </summary>
public class PersonMinimal
{
    [JsonPropertyName("@type")] public string Type { get; set; } = "Person";
    [JsonPropertyName("givenName")] public string? GivenName { get; set; }
    [JsonPropertyName("familyName")] public string? FamilyName { get; set; }
    [JsonPropertyName("birthDate")] public string? BirthDate { get; set; } // ISO8601 date
}

/// <summary>
/// Contact coordinates for order-related communication.
/// </summary>
public class ContactPoint
{
    [JsonPropertyName("@type")] public string Type { get; set; } = "ContactPoint";
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("telephone")] public string? Telephone { get; set; }
}

/// <summary>
/// Client-maintained shopping basket used to assemble an order prior to checkout.
/// </summary>
public class Basket
{
    [JsonPropertyName("@context")] public string[] Context { get; init; } = CartJsonLd.BasketContext;
    [JsonPropertyName("@type")] public string Type { get; init; } = "circles:Basket";

    [JsonPropertyName("basketId")] public string BasketId { get; init; } = string.Empty;
    [JsonPropertyName("buyer")] public string? Buyer { get; set; }
    [JsonPropertyName("operator")] public string? Operator { get; set; }
    [JsonPropertyName("chainId")] public long ChainId { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = nameof(BasketStatus.Draft);
    [JsonPropertyName("version")] public long Version { get; set; } = 0;

    [JsonPropertyName("items")] public List<OrderItemPreview> Items { get; set; } = new();
    [JsonPropertyName("customer")] public PersonMinimal? Customer { get; set; }
    [JsonPropertyName("shippingAddress")] public PostalAddress? ShippingAddress { get; set; }
    [JsonPropertyName("billingAddress")] public PostalAddress? BillingAddress { get; set; }
    [JsonPropertyName("ageProof")] public PersonMinimal? AgeProof { get; set; }
    [JsonPropertyName("contactPoint")] public ContactPoint? ContactPoint { get; set; }

    [JsonPropertyName("createdAt")] public long CreatedAt { get; init; }
    [JsonPropertyName("modifiedAt")] public long ModifiedAt { get; set; }
    [JsonPropertyName("ttlSeconds")] public int TtlSeconds { get; set; } = 86400;
}

/// <summary>
/// Request payload to create a new basket owned by a buyer/operator on a chain.
/// </summary>
public class BasketCreateRequest
{
    [JsonPropertyName("operator")] public string? Operator { get; set; }
    [JsonPropertyName("buyer")] public string? Buyer { get; set; }
    [JsonPropertyName("chainId")] public long? ChainId { get; set; }
}

/// <summary>
/// Response returned after creating a basket; includes the generated <c>basketId</c>.
/// </summary>
public class BasketCreateResponse
{
    [JsonPropertyName("@type")] public string Type { get; init; } = "circles:Basket";
    [JsonPropertyName("basketId")] public string BasketId { get; init; } = string.Empty;
}

/// <summary>
/// A single validation requirement describing a missing or invalid slot in the basket.
/// </summary>
public class ValidationRequirement
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("ruleId")] public string RuleId { get; set; } = string.Empty;
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("slot")] public string Slot { get; set; } = string.Empty;
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    [JsonPropertyName("expectedTypes")] public string[] ExpectedTypes { get; set; } = Array.Empty<string>();
    [JsonPropertyName("cardinality")] public Cardinality Cardinality { get; set; } = new();
    [JsonPropertyName("status")] public string Status { get; set; } = "missing"; // ok | missing | typeMismatch | invalidShape
    [JsonPropertyName("foundAt")] public string? FoundAt { get; set; }
    [JsonPropertyName("foundType")] public string? FoundType { get; set; }

    // Future-proofing for per-item validation without breaking existing clients
    [JsonPropertyName("scope")] public string? Scope { get; set; }
    [JsonPropertyName("itemIndexes")] public List<int>? ItemIndexes { get; set; }

    // Optional flag for future non-blocking recommendations; defaults to blocking today
    [JsonPropertyName("blocking")] public bool Blocking { get; set; } = true;
}

/// <summary>
/// Expected multiplicity of a field or collection.
/// </summary>
public class Cardinality
{
    [JsonPropertyName("min")] public int Min { get; set; } = 1;
    [JsonPropertyName("max")] public int Max { get; set; } = 1;
}

/// <summary>
/// Trace entry for a validation rule evaluation.
/// </summary>
public class RuleTrace
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("evaluated")] public bool Evaluated { get; set; }
    [JsonPropertyName("result")] public string Result { get; set; } = string.Empty;
}

/// <summary>
/// Output of the basket validation flow with per-slot diagnostics.
/// </summary>
public class ValidationResult
{
    [JsonPropertyName("@context")] public string Context { get; init; } = "https://schema.org/";
    [JsonPropertyName("@type")] public string Type { get; init; } = "Thing";
    [JsonPropertyName("basketId")] public string BasketId { get; set; } = string.Empty;
    [JsonPropertyName("valid")] public bool Valid { get; set; }
    [JsonPropertyName("requirements")] public List<ValidationRequirement> Requirements { get; set; } = new();
    [JsonPropertyName("missing")] public List<MissingSlot> Missing { get; set; } = new();
    [JsonPropertyName("ruleTrace")] public List<RuleTrace> RuleTrace { get; set; } = new();
}

/// <summary>
/// Minimal description of an expected slot that is absent from the basket.
/// </summary>
public class MissingSlot
{
    [JsonPropertyName("slot")] public string Slot { get; set; } = string.Empty;
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    [JsonPropertyName("expectedTypes")] public string[] ExpectedTypes { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Immutable order snapshot materialized at checkout (schema.org <c>Order</c> shape).
/// </summary>
/// <remarks>
/// Includes a public, non-secret <c>paymentReference</c> (format: <c>pay_</c> + 32 hex).
/// Use it to correlate on-chain payments via memo/data fields. Do not embed any secrets or tokens in URLs.
/// Full order reads are authorized via JWT-bound ownership checks using the public <c>orderId</c>.
/// </remarks>
public class OrderSnapshot
{
    [JsonPropertyName("@context")] public string Context { get; init; } = "https://schema.org/";
    [JsonPropertyName("@type")] public string Type { get; init; } = "Order";

    /// <summary>
    /// Public order identifier. Set to the internal orderId (e.g., ord_ + 32 hex).
    /// </summary>
    [JsonPropertyName("orderNumber")] public string OrderNumber { get; set; } = string.Empty;
    [JsonPropertyName("orderStatus")] public string OrderStatus { get; set; } = "https://schema.org/OrderPaymentDue";

    // Public non-secret reference to correlate on-chain payments with this order
    [JsonPropertyName("paymentReference")] public string PaymentReference { get; set; } = string.Empty;

    // ISO 8601 date-time in UTC when the order was created
    [JsonPropertyName("orderDate")] public string? OrderDate { get; set; }

    [JsonPropertyName("customer")] public SchemaOrgPersonId Customer { get; set; } = new();
    [JsonPropertyName("broker")] public SchemaOrgOrgId Broker { get; set; } = new();

    [JsonPropertyName("acceptedOffer")] public List<OfferSnapshot> AcceptedOffer { get; set; } = new();
    [JsonPropertyName("orderedItem")] public List<OrderItemLine> OrderedItem { get; set; } = new();

    [JsonPropertyName("billingAddress")] public PostalAddress? BillingAddress { get; set; }
    [JsonPropertyName("shippingAddress")] public PostalAddress? ShippingAddress { get; set; }

    [JsonPropertyName("totalPaymentDue")] public PriceSpecification? TotalPaymentDue { get; set; }
    [JsonPropertyName("paymentUrl")] public string? PaymentUrl { get; set; }

    // Optional: secrets exposed only through authorized endpoints
    [JsonPropertyName("voucherCode")] public string? VoucherCode { get; set; }
    [JsonPropertyName("downloadUrl")] public string? DownloadUrl { get; set; }
    [JsonPropertyName("sellerContact")] public ContactPoint? SellerContact { get; set; }

    // Generic JSON-LD outbox items attached to the order (buyer-visible only)
    [JsonPropertyName("outbox")]
    public List<OrderOutboxItemDto> Outbox { get; set; } = new();
}

public sealed class OrderOutboxItemDto
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("payload")] public System.Text.Json.JsonElement Payload { get; set; }
}

/// <summary>
/// Monetary total of the order using schema.org <c>PriceSpecification</c>.
/// </summary>
public class PriceSpecification
{
    [JsonPropertyName("@type")] public string Type { get; set; } = "PriceSpecification";
    [JsonPropertyName("price")] public decimal? Price { get; set; }
    [JsonPropertyName("priceCurrency")] public string? PriceCurrency { get; set; }
}

/// <summary>
/// Single line of the composed order with quantity and product reference.
/// </summary>
public class OrderItemLine
{
    [JsonPropertyName("@type")] public string Type { get; set; } = "OrderItem";
    [JsonPropertyName("orderQuantity")] public int OrderQuantity { get; set; }
    [JsonPropertyName("orderedItem")] public OrderedItemRef OrderedItem { get; set; } = new();
    [JsonPropertyName("productCid")] public string? ProductCid { get; set; }
}

/// <summary>
/// Schema.org Person identifier wrapper using JSON-LD <c>@id</c>.
/// Example: <c>eip155:100:0xabc…</c>
/// </summary>
public class SchemaOrgPersonId
{
    [JsonPropertyName("@type")] public string Type { get; set; } = "Person";
    [JsonPropertyName("@id")] public string? Id { get; set; }
    [JsonPropertyName("givenName")] public string? GivenName { get; set; }
    [JsonPropertyName("familyName")] public string? FamilyName { get; set; }
}
