namespace Circles.Market.Api.Routing;

public sealed record MarketRouteConfig(
    long ChainId,
    string SellerAddress,
    string Sku,
    string? OfferType,
    bool IsOneOff,
    bool Enabled)
{
    public bool IsConfigured => Enabled && (IsOneOff || !string.IsNullOrWhiteSpace(OfferType));
}
