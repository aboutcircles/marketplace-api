using Circles.Market.Api.Routing;

namespace Circles.Market.Tests;

[TestFixture]
public class MarketRouteStoreConfiguredSemanticsTests
{
    private sealed class FakeRouteStore : IMarketRouteStore
    {
        private readonly Dictionary<(long chain, string seller, string sku), MarketRouteConfig> _routes = new();
        private readonly Dictionary<string, bool> _offerTypesEnabled = new(StringComparer.OrdinalIgnoreCase);

        public void SetOfferType(string offerType, bool enabled)
        {
            _offerTypesEnabled[offerType] = enabled;
        }

        public void SetRoute(MarketRouteConfig cfg)
        {
            _routes[(cfg.ChainId, cfg.SellerAddress, cfg.Sku)] = cfg;
        }

        public Task EnsureSchemaAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<MarketRouteConfig?> TryGetAsync(long chainId, string sellerAddress, string sku, CancellationToken ct = default)
        {
            _routes.TryGetValue((chainId, sellerAddress, sku), out var cfg);
            return Task.FromResult<MarketRouteConfig?>(cfg);
        }

        public Task<string?> TryResolveUpstreamAsync(long chainId, string sellerAddress, string sku, MarketServiceKind serviceKind, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public async Task<bool> IsConfiguredAsync(long chainId, string sellerAddress, string sku, CancellationToken ct = default)
        {
            var cfg = await TryGetAsync(chainId, sellerAddress, sku, ct);
            if (cfg is null) { return false; }
            if (!cfg.Enabled) { return false; }
            if (cfg.IsOneOff) { return true; }
            if (string.IsNullOrWhiteSpace(cfg.OfferType)) { return false; }
            return _offerTypesEnabled.TryGetValue(cfg.OfferType, out var enabled) && enabled;
        }

        public Task<IReadOnlyList<MarketSellerAddress>> GetActiveSellersAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MarketSellerAddress>>(Array.Empty<MarketSellerAddress>());
    }

    [Test]
    public async Task IsConfiguredAsync_ReturnsFalse_When_OfferTypeUnknown()
    {
        var store = new FakeRouteStore();
        store.SetOfferType("odoo", enabled: true);

        store.SetRoute(new MarketRouteConfig(
            ChainId: 100,
            SellerAddress: "0xseller",
            Sku: "sku",
            OfferType: "typo",
            IsOneOff: false,
            Enabled: true));

        var ok = await store.IsConfiguredAsync(100, "0xseller", "sku");
        Assert.That(ok, Is.False);
    }

    [Test]
    public async Task IsConfiguredAsync_ReturnsTrue_When_OfferTypeKnownAndEnabled()
    {
        var store = new FakeRouteStore();
        store.SetOfferType("odoo", enabled: true);

        store.SetRoute(new MarketRouteConfig(
            ChainId: 100,
            SellerAddress: "0xseller",
            Sku: "sku",
            OfferType: "odoo",
            IsOneOff: false,
            Enabled: true));

        var ok = await store.IsConfiguredAsync(100, "0xseller", "sku");
        Assert.That(ok, Is.True);
    }

    [Test]
    public async Task IsConfiguredAsync_ReturnsTrue_For_OneOff_Even_When_OfferTypeMissing()
    {
        var store = new FakeRouteStore();

        store.SetRoute(new MarketRouteConfig(
            ChainId: 100,
            SellerAddress: "0xseller",
            Sku: "sku",
            OfferType: null,
            IsOneOff: true,
            Enabled: true));

        var ok = await store.IsConfiguredAsync(100, "0xseller", "sku");
        Assert.That(ok, Is.True);
    }

    [Test]
    public void MarketRouteConfig_CanCarry_TotalInventory_Metadata()
    {
        var cfg = new MarketRouteConfig(
            ChainId: 100,
            SellerAddress: "0xseller",
            Sku: "sku",
            OfferType: "odoo",
            IsOneOff: false,
            Enabled: true,
            TotalInventory: 123);

        Assert.That(cfg.TotalInventory, Is.EqualTo(123));
        Assert.That(cfg.IsConfigured, Is.True);
    }
}
