using System.Text.Json.Serialization;

namespace Circles.Market.Api.Cart.SellerVisibility;

// Seller view DTO: safe-by-construction. Never return OrderSnapshot to sellers.
public sealed class SellerOrderDto
{
    [JsonPropertyName("@context")] public string Context { get; init; } = "https://schema.org/";
    [JsonPropertyName("@type")] public string Type { get; init; } = "Order";

    [JsonPropertyName("orderNumber")] public string OrderNumber { get; set; } = string.Empty;
    [JsonPropertyName("orderStatus")] public string? OrderStatus { get; set; }
    [JsonPropertyName("orderDate")] public string? OrderDate { get; set; }
        = null; // will be copied from internal snapshot (DB created_at overlay)

    [JsonPropertyName("paymentReference")] public string? PaymentReference { get; set; }

    [JsonPropertyName("broker")] public SchemaOrgOrgId? Broker { get; set; }

    // Filtered arrays – contain only lines belonging to this seller
    [JsonPropertyName("acceptedOffer")] public List<OfferSnapshot> AcceptedOffer { get; set; } = new();
    [JsonPropertyName("orderedItem")] public List<OrderItemLine> OrderedItem { get; set; } = new();

    // Monetary totals: conservative – omit cross-seller sums by default
    [JsonPropertyName("totalPaymentDue")] public PriceSpecification? TotalPaymentDue { get; set; }

    // Outbox: optional; default to empty or filtered at builder level
    [JsonPropertyName("outbox")] public List<OrderOutboxItemDto> Outbox { get; set; } = new();
}
