namespace Circles.Market.Api.Routing;

public sealed record MarketRouteConfig(
    long ChainId,
    string SellerAddress,
    string Sku,
    string? InventoryUrl,
    string? AvailabilityUrl,
    string? FulfillmentUrl,
    bool IsOneOff,
    bool Enabled)
{
    public bool IsConfigured => Enabled && (IsOneOff || !string.IsNullOrWhiteSpace(InventoryUrl) ||
                                           !string.IsNullOrWhiteSpace(AvailabilityUrl) ||
                                           !string.IsNullOrWhiteSpace(FulfillmentUrl));
}
