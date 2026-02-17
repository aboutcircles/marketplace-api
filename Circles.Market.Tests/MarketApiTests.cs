using System.Text;
using System.Text.Json;
using Circles.Market.Api.Catalog;
using Circles.Market.Api.Inventory;
using Circles.Market.Api.Routing;
using Circles.Market.Tests.Mocks;
using Circles.Profiles.Aggregation;
using Circles.Profiles.Interfaces;
using Circles.Profiles.Models.Core;
using Circles.Profiles.Models.Market;
using Microsoft.AspNetCore.Http;
using Moq;

namespace Circles.Market.Tests;

internal class AlwaysConfiguredRouteStore : IMarketRouteStore
{
    public Task EnsureSchemaAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<bool> IsConfiguredAsync(long chainId, string sellerAddress, string sku, CancellationToken ct = default)
        => Task.FromResult(true);

    public Task<string?> TryResolveUpstreamAsync(long chainId, string sellerAddress, string sku, MarketServiceKind serviceKind,
        CancellationToken ct = default) => Task.FromResult<string?>(null);

    public Task<MarketRouteConfig?> TryGetAsync(long chainId, string sellerAddress, string sku, CancellationToken ct = default)
        => Task.FromResult<MarketRouteConfig?>(new MarketRouteConfig(
            ChainId: chainId,
            SellerAddress: sellerAddress.Trim().ToLowerInvariant(),
            Sku: sku.Trim().ToLowerInvariant(),
            OfferType: "odoo",
            IsOneOff: false,
            Enabled: true));

    public Task<IReadOnlyList<MarketSellerAddress>> GetActiveSellersAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MarketSellerAddress>>(new List<MarketSellerAddress>());
}

[TestFixture]
public class MarketApiTests
{
    private static Circles.Profiles.Market.CatalogReducer NewReducer(InMemoryIpfsStore ipfs)
    {
        return new Circles.Profiles.Market.CatalogReducer(ipfs);
    }

    private static CustomDataLink MakeLink(string name, string cid, long signedAt)
    {
        return new CustomDataLink
        {
            Name = name,
            Cid = cid,
            ChainId = 100,
            SignedAt = signedAt,
            Nonce = CustomDataLink.NewNonce(),
            SignerAddress = "0xdeadbeef",
            Signature = "0x01"
        };
    }

    /* 1) Ordering tiebreaker */
    [Test]
    public async Task Ordering_Uses_IndexInChunk_As_TieBreaker()
    {
        var ipfs = new InMemoryIpfsStore();
        // Minimal valid product payloads
        string prodJson = JsonSerializer.Serialize(new SchemaOrgProduct
        {
            Name = "Tee",
            Sku = "tee-1",
            Offers = { new SchemaOrgOffer { PriceCurrency = "EUR" } }
        }, Circles.Profiles.Models.JsonSerializerOptions.JsonLd);
        string cid = await ipfs.AddStringAsync(prodJson, pin: true);

        var reducer = NewReducer(ipfs);

        long ts = 1_700_000_000;
        var l1 = new LinkWithProvenance("0xaaa", "chunk", 7, MakeLink("product/tee-1", cid, ts), "0xk1");
        var l2 = new LinkWithProvenance("0xaaa", "chunk", 3, MakeLink("product/tee-1", cid, ts), "0xk2");

        var errors = new List<JsonElement>();
        var (items, _) = (await reducer.ReduceAsync(new List<LinkWithProvenance> { l1, l2 }, errors, CancellationToken.None));

        Assert.That(items, Has.Count.EqualTo(1));
        Assert.That(items[0].IndexInChunk, Is.EqualTo(7));
    }

    /* 2) Link name + payload SKU */
    [Test]
    public async Task Product_Sku_Must_Match_Link_Name_Ok_And_Errors()
    {
        var ipfs = new InMemoryIpfsStore();

        string mkProd(string sku) => JsonSerializer.Serialize(new SchemaOrgProduct
        {
            Name = "Any",
            Sku = sku,
            Offers = { new SchemaOrgOffer { PriceCurrency = "EUR" } }
        }, Circles.Profiles.Models.JsonSerializerOptions.JsonLd);

        string cidOk = await ipfs.AddStringAsync(mkProd("tee-1"), true);
        string cidBad = await ipfs.AddStringAsync(mkProd("tee-2"), true);

        long ts = 1_700_000_000;
        var okLink = new LinkWithProvenance("0xaaa", "chunk", 1, MakeLink("product/tee-1", cidOk, ts), "0xk1");
        var badLink = new LinkWithProvenance("0xaaa", "chunk", 1, MakeLink("product/tee-1", cidBad, ts), "0xk2");

        var reducer = NewReducer(ipfs);
        var errs = new List<JsonElement>();
        var _ = await reducer.ReduceAsync(new List<LinkWithProvenance> { okLink, badLink }, errs, CancellationToken.None);

        // one payload error for the mismatch
        Assert.That(errs.Count(e => e.GetProperty("stage").GetString() == "payload"), Is.EqualTo(1));
    }

    [Test]
    public async Task Tombstone_Sku_Must_Match_Link_Name()
    {
        var ipfs = new InMemoryIpfsStore();

        string mkTomb(string sku) => JsonSerializer.Serialize(new {
            @context = Circles.Profiles.Models.JsonLdMeta.MarketContext,
            @type = "Tombstone",
            sku
        });

        string cidBad = await ipfs.AddStringAsync(mkTomb("tee-2"), false);
        long ts = 1_700_000_000;
        var tLink = new LinkWithProvenance("0xaaa", "chunk", 1, MakeLink("product/tee-1", cidBad, ts), "0xk1");

        var reducer = NewReducer(ipfs);
        var errs = new List<JsonElement>();
        var _ = await reducer.ReduceAsync(new List<LinkWithProvenance> { tLink }, errs, CancellationToken.None);

        Assert.That(errs.Count(e => e.GetProperty("stage").GetString() == "payload"), Is.EqualTo(1));
    }

    /* 3) Negative cursor and headers */
    [Test]
    public async Task Cursor_Negative_Is_400_And_No_Vary_Header()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.QueryString = new QueryString("?avatars=0xabc");

        string cur = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { start = -1 }));
        ctx.Response.Body = new MemoryStream();

        var mockInventoryClient = new Mock<ILiveInventoryClient>();
        await OperatorCatalogEndpoint.Handle("0xop", 100, 0, 0, 10, cur, null, null, ctx, new AlwaysConfiguredRouteStore(), null!, mockInventoryClient.Object, CancellationToken.None);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
        ctx.Response.Body.Position = 0;
        string body = await new StreamReader(ctx.Response.Body, Encoding.UTF8).ReadToEndAsync();
        var err = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.That(err.GetProperty("error").GetString(), Is.EqualTo("cursor.start must be >= 0"));
        Assert.That(ctx.Response.Headers.ContainsKey("Vary"), Is.False);
    }

    [Test]
    public async Task Success_Response_Has_No_Vary_Header()
    {
        var ipfs = new InMemoryIpfsStore();
        // Provide a BasicAggregator that will quickly yield no links (mock registry returning empty)
        var reg = new Mock<INameRegistry>();
        reg.Setup(r => r.GetProfileCidAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        var sig = new Mock<ISignatureVerifier>().Object;
        var basic = new BasicAggregator(ipfs, reg.Object, sig);
        var reducer = new Profiles.Market.CatalogReducer(ipfs);
        var svc = new Profiles.Market.OperatorCatalogService(basic, reducer);

        var ctx = new DefaultHttpContext();
        ctx.Request.QueryString = new QueryString("?avatars=0x1234567890123456789012345678901234567890");
        ctx.Response.Body = new MemoryStream();

        var routes = new AlwaysConfiguredRouteStore();
        var mockInventoryClient = new Mock<ILiveInventoryClient>();
        await OperatorCatalogEndpoint.Handle("0x1234567890123456789012345678901234567890", 100, 0, (long?)null, 10, null, 0, null, ctx, routes, svc, mockInventoryClient.Object, CancellationToken.None);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(ctx.Response.Headers.ContainsKey("Vary"), Is.False);
    }
}
