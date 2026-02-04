namespace Circles.Market.Api.Routing;

public interface IMarketRouteStore
{
    Task EnsureSchemaAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns true when a (chainId, seller, sku) tuple is enabled and has at least one configured service route
    /// (or is marked as one-off).
    /// </summary>
    Task<bool> IsConfiguredAsync(long chainId, string sellerAddress, string sku, CancellationToken ct = default);

    /// <summary>
    /// Resolves the upstream URL for a configured (chainId, seller, sku) tuple and service kind.
    /// Returns null when the tuple is not configured/enabled or when that particular service kind is not configured.
    /// </summary>
    Task<string?> TryResolveUpstreamAsync(long chainId, string sellerAddress, string sku, MarketServiceKind serviceKind,
        CancellationToken ct = default);

    /// <summary>
    /// Resolves the DB configuration for a (chainId, seller, sku) tuple.
    /// Returns null when no enabled configuration exists.
    /// </summary>
    Task<MarketRouteConfig?> TryGetAsync(long chainId, string sellerAddress, string sku, CancellationToken ct = default);

    /// <summary>
    /// Returns all active seller addresses that have at least one enabled route configuration.
    /// </summary>
    Task<IReadOnlyList<MarketSellerAddress>> GetActiveSellersAsync(CancellationToken ct = default);
}
