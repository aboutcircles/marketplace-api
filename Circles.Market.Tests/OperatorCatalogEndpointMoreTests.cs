using System.Text;
using System.Text.Json;
using Circles.Market.Api.Catalog;
using Circles.Market.Api.Inventory;
using Microsoft.AspNetCore.Http;
using Moq;

namespace Circles.Market.Tests;

[TestFixture]
public class OperatorCatalogEndpointMoreTests
{
    [Test]
    public async Task Cursor_Invalid_Base64_Is_400()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.QueryString = new QueryString("?avatars=0xabc&cursor=not-base64");
        ctx.Response.Body = new MemoryStream();

        var routes = new AlwaysConfiguredRouteStore();
        var mockInventoryClient = new Mock<ILiveInventoryClient>();
        await OperatorCatalogEndpoint.Handle("0xop", 100, 0, 0, 10, "not-base64", null, null, ctx, routes, (Circles.Profiles.Market.OperatorCatalogService)null!, mockInventoryClient.Object, CancellationToken.None);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
        ctx.Response.Body.Position = 0;
        string body = await new StreamReader(ctx.Response.Body, Encoding.UTF8).ReadToEndAsync();
        var err = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.That(err.GetProperty("error").GetString(), Is.EqualTo("Invalid cursor"));
    }

    [Test]
    public async Task Offset_Negative_Is_400()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.QueryString = new QueryString("?avatars=0xabc&offset=-1");
        ctx.Response.Body = new MemoryStream();

        var routes = new AlwaysConfiguredRouteStore();
        var mockInventoryClient = new Mock<ILiveInventoryClient>();
        await OperatorCatalogEndpoint.Handle("0xop", 100, 0, 0, 10, null, -1, null, ctx, routes, (Circles.Profiles.Market.OperatorCatalogService)null!, mockInventoryClient.Object, CancellationToken.None);

        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
        ctx.Response.Body.Position = 0;
        string body = await new StreamReader(ctx.Response.Body, Encoding.UTF8).ReadToEndAsync();
        var err = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.That(err.GetProperty("error").GetString(), Is.EqualTo("offset must be >= 0"));
    }
}
