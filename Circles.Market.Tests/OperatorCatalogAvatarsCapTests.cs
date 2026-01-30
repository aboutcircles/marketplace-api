using System.Text;
using System.Text.Json;
using Circles.Market.Api.Catalog;
using Circles.Market.Tests.Mocks;
using Microsoft.AspNetCore.Http;
using Moq;

namespace Circles.Market.Tests;

[TestFixture]
public class OperatorCatalogAvatarsCapTests
{
    [TearDown]
    public void Teardown()
    {
        Environment.SetEnvironmentVariable("CATALOG_MAX_AVATARS", null);
    }

    [Test]
    public async Task Default_Cap_500_Enforced_501_Is_400()
    {
        Environment.SetEnvironmentVariable("CATALOG_MAX_AVATARS", null);

        var ctx = new DefaultHttpContext();
        // Build query with 501 avatars
        var q = new StringBuilder("?avatars=" + new string('a', 42));
        for (int i = 0; i < 500; i++) q.Append("&avatars=" + new string('b', 42));
        ctx.Request.QueryString = new QueryString(q.ToString());
        ctx.Response.Body = new MemoryStream();

        var routes = new AlwaysConfiguredRouteStore();
        await OperatorCatalogEndpoint.Handle("0xop", 100, 0, 0, 10, null, null, ctx, routes, (Circles.Profiles.Market.OperatorCatalogService)null!, CancellationToken.None);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
        ctx.Response.Body.Position = 0;
        var text = await new StreamReader(ctx.Response.Body, Encoding.UTF8).ReadToEndAsync();
        var err = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.That(err.GetProperty("error").GetString(), Does.Contain("avatars must be <= 500"));
    }

    [Test]
    public async Task Cap_500_Allows_Exactly_500()
    {
        Environment.SetEnvironmentVariable("CATALOG_MAX_AVATARS", null);

        var ctx = new DefaultHttpContext();
        // Exactly 500 avatars
        var q = new StringBuilder("?avatars=" + new string('a', 42));
        for (int i = 0; i < 499; i++) q.Append("&avatars=" + new string('b', 42));
        ctx.Request.QueryString = new QueryString(q.ToString());
        ctx.Response.Body = new MemoryStream();

        // Use real but lightweight service: BasicAggregator with empty registry and real reducer
        var ipfs = new InMemoryIpfsStore();
        var reg = new Moq.Mock<Circles.Profiles.Interfaces.INameRegistry>();
        reg.Setup(r => r.GetProfileCidAsync(Moq.It.IsAny<string>(), Moq.It.IsAny<CancellationToken>()))
           .ReturnsAsync((string?)null);
        var sig = new Moq.Mock<Circles.Profiles.Interfaces.ISignatureVerifier>().Object;
        var basic = new Circles.Profiles.Aggregation.BasicAggregator(ipfs, reg.Object, sig);
        var reducer = new Circles.Profiles.Market.CatalogReducer(ipfs);
        var svc = new Circles.Profiles.Market.OperatorCatalogService(basic, reducer);

        var routes2 = new AlwaysConfiguredRouteStore();
        await OperatorCatalogEndpoint.Handle("0x1234567890123456789012345678901234567890", 100, 0, 0, 10, null, null, ctx, routes2, svc, CancellationToken.None);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
    }

    [Test]
    public async Task Env_Override_Works()
    {
        Environment.SetEnvironmentVariable("CATALOG_MAX_AVATARS", "2");

        var ctx = new DefaultHttpContext();
        ctx.Request.QueryString = new QueryString("?avatars=0x1&avatars=0x2&avatars=0x3");
        ctx.Response.Body = new MemoryStream();

        var routes3 = new AlwaysConfiguredRouteStore();
        await OperatorCatalogEndpoint.Handle("0xop", 100, 0, 0, 10, null, null, ctx, routes3, (Circles.Profiles.Market.OperatorCatalogService)null!, CancellationToken.None);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
        ctx.Response.Body.Position = 0;
        var text = await new StreamReader(ctx.Response.Body, Encoding.UTF8).ReadToEndAsync();
        var err = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.That(err.GetProperty("error").GetString(), Does.Contain("avatars must be <= 2"));
    }
}
