using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Circles.Market.Shared;

public sealed class FulfillmentRequest
{
    [Required]
    public string OrderId { get; set; } = string.Empty;

    [Required]
    public string PaymentReference { get; set; } = string.Empty;

    public string? Buyer { get; set; }

    [Required]
    public List<FulfillmentItem> Items { get; set; } = new();

    [Required]
    public string Trigger { get; set; } = string.Empty;

    public bool TryNormalizeAndValidate(out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(OrderId)) { error = "orderId is required"; return false; }
        if (string.IsNullOrWhiteSpace(PaymentReference)) { error = "paymentReference is required"; return false; }
        if (Items is null || Items.Count == 0) { error = "items must contain at least one element"; return false; }
        foreach (var it in Items)
        {
            if (string.IsNullOrWhiteSpace(it.Sku)) { error = "item.sku is required"; return false; }
            it.Sku = it.Sku.Trim().ToLowerInvariant();
        }
        if (string.IsNullOrWhiteSpace(Trigger)) { error = "trigger is required"; return false; }
        var t = Trigger.Trim().ToLowerInvariant();
        if (t != "confirmed" && t != "finalized") { error = "trigger must be 'confirmed' or 'finalized'"; return false; }
        Trigger = t;
        return true;
    }
}

public sealed class FulfillmentItem
{
    public string Sku { get; set; } = string.Empty;

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals)]
    public decimal Quantity { get; set; } = 1;
}
