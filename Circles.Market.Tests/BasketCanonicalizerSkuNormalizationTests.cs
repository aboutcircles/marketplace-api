using Circles.Market.Api.Cart;
using Circles.Market.Api.Inventory;
using Circles.Market.Api.Routing;
using Circles.Profiles.Models.Market;
using Microsoft.Extensions.Caching.Memory;
using Moq;

namespace Circles.Market.Tests;

[TestFixture]
public class BasketCanonicalizerSkuNormalizationTests
{
    [Test]
    public async Task CanonicalizeAsync_GroupsItemsWithCasingDifferences_SameProduct()
    {
        // Arrange
        var mockResolver = new Mock<IProductResolver>();
        var mockInventoryClient = new Mock<ILiveInventoryClient>();
        var mockRoutes = new Mock<IMarketRouteStore>();
        var cache = new MemoryCache(new MemoryCacheOptions());

        var canonicalizer = new BasketCanonicalizer(
            mockResolver.Object,
            mockInventoryClient.Object,
            mockRoutes.Object,
            cache);

        // Product with SKU "abc"
        var productAbc = new SchemaOrgProduct
        {
            Name = "Test",
            Sku = "abc",
            Offers = { new SchemaOrgOffer { PriceCurrency = "EUR", Price = 10m } }
        };
        var cidAbc = "cid-abc";

        // Product with SKU "ABC" (different casing)
        var productAbcUpper = new SchemaOrgProduct
        {
            Name = "Test",
            Sku = "ABC",
            Offers = { new SchemaOrgOffer { PriceCurrency = "EUR", Price = 10m } }
        };
        var cidAbcUpper = "cid-abc-upper";

        // Both SKUs resolve to same canonical product (lowercase)
        mockResolver
            .Setup(r => r.ResolveProductAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long chainId, string seller, string? op, string sku, CancellationToken ct) =>
            {
                if (sku.Equals("abc", StringComparison.OrdinalIgnoreCase))
                    return (productAbc, cidAbc);
                return ((SchemaOrgProduct?)null, null)!;
            });

        mockRoutes
            .Setup(r => r.TryGetAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long chainId, string seller, string sku, CancellationToken ct) =>
                new MarketRouteConfig(chainId, seller, sku, "odoo", false, true));

        mockRoutes
            .Setup(r => r.TryResolveUpstreamAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MarketServiceKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long chainId, string seller, string sku, MarketServiceKind kind, CancellationToken ct) =>
                $"http://inventory/{sku}");

        // Return inventory = 10 for all requests
        mockInventoryClient
            .Setup(i => i.FetchInventoryAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string url, CancellationToken ct) => (false, null, new Circles.Profiles.Models.Market.SchemaOrgQuantitativeValue { Value = 10 }));

        var basket = new Basket
        {
            BasketId = "test-basket",
            ChainId = 100,
            Items = new List<OrderItemPreview>
            {
                new()
                {
                    Seller = "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    OrderedItem = new OrderedItemRef { Sku = "AbC" }, // mixed case
                    OrderQuantity = 2
                },
                new()
                {
                    Seller = "0xAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                    OrderedItem = new OrderedItemRef { Sku = "ABC" }, // uppercase
                    OrderQuantity = 3
                }
            }
        };

        // Act
        await canonicalizer.CanonicalizeAsync(basket, CancellationToken.None);

        // Assert - inventory should be fetched only once (not twice) because SKUs are normalized
        mockInventoryClient.Verify(
            i => i.FetchInventoryAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "Inventory fetch should be called exactly once because AbC and ABC should map to the same product key");

        // Total quantity = 5 (2 + 3)
        Assert.That(basket.Items[0].OrderQuantity, Is.EqualTo(2));
        Assert.That(basket.Items[1].OrderQuantity, Is.EqualTo(3));
    }

    [Test]
    public async Task CanonicalizeAsync_RejectsExcessQuantity_WhenDifferentCasing_SameProduct()
    {
        // Arrange
        var mockResolver = new Mock<IProductResolver>();
        var mockInventoryClient = new Mock<ILiveInventoryClient>();
        var mockRoutes = new Mock<IMarketRouteStore>();
        var cache = new MemoryCache(new MemoryCacheOptions());

        var canonicalizer = new BasketCanonicalizer(
            mockResolver.Object,
            mockInventoryClient.Object,
            mockRoutes.Object,
            cache);

        var product = new SchemaOrgProduct
        {
            Name = "Test",
            Sku = "abc",
            Offers = { new SchemaOrgOffer { PriceCurrency = "EUR", Price = 10m } }
        };

        mockResolver
            .Setup(r => r.ResolveProductAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long chainId, string seller, string? op, string sku, CancellationToken ct) =>
                (sku.Equals("abc", StringComparison.OrdinalIgnoreCase) ? (product, "cid") : ((null, null)!)));

        mockRoutes
            .Setup(r => r.TryGetAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long chainId, string seller, string sku, CancellationToken ct) =>
                new MarketRouteConfig(chainId, seller, sku, "odoo", false, true));

        mockRoutes
            .Setup(r => r.TryResolveUpstreamAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MarketServiceKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("http://inventory/abc");

        // Only 5 available
        mockInventoryClient
            .Setup(i => i.FetchInventoryAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string url, CancellationToken ct) => (false, null, new Circles.Profiles.Models.Market.SchemaOrgQuantitativeValue { Value = 5 }));

        var basket = new Basket
        {
            BasketId = "test-basket",
            ChainId = 100,
            Items = new List<OrderItemPreview>
            {
                new()
                {
                    Seller = "0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                    OrderedItem = new OrderedItemRef { Sku = "AbC" },
                    OrderQuantity = 3
                },
                new()
                {
                    Seller = "0xBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB",
                    OrderedItem = new OrderedItemRef { Sku = "ABC" },
                    OrderQuantity = 3 // Would exceed if grouped (6 > 5), or pass if not grouped
                }
            }
        };

        // Act & Assert - should throw because 3 + 3 = 6 exceeds inventory of 5
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await canonicalizer.CanonicalizeAsync(basket, CancellationToken.None),
            "Should group AbC and ABC together and reject excess quantity");
    }
}
