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

[TestFixture]
public class OperatorCatalogTotalAvailabilityTests
{
    [Test]
    public async Task IncludeTotalAvailability_False_Does_Not_Include_Field()
    {
        // Arrange
        var ipfs = new InMemoryIpfsStore();
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

        // Act - includeTotalAvailability = false (default)
        await OperatorCatalogEndpoint.Handle(
            "0x1234567890123456789012345678901234567890", 
            100, 0, null, 10, null, 0, false, 
            ctx, routes, svc, mockInventoryClient.Object, CancellationToken.None);

        // Assert
        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        ctx.Response.Body.Position = 0;
        var responseText = await new StreamReader(ctx.Response.Body, Encoding.UTF8).ReadToEndAsync();
        
        // Verify that totalAvailability is NOT in the response
        Assert.That(responseText, Does.Not.Contain("totalAvailability"));
        
        // Verify inventory client was not called
        mockInventoryClient.Verify(
            x => x.FetchInventoryAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), 
            Times.Never);
    }

    [Test]
    public async Task IncludeTotalAvailability_Null_Does_Not_Include_Field()
    {
        // Arrange
        var ipfs = new InMemoryIpfsStore();
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

        // Act - includeTotalAvailability = null (default)
        await OperatorCatalogEndpoint.Handle(
            "0x1234567890123456789012345678901234567890", 
            100, 0, null, 10, null, 0, null, 
            ctx, routes, svc, mockInventoryClient.Object, CancellationToken.None);

        // Assert
        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        ctx.Response.Body.Position = 0;
        var responseText = await new StreamReader(ctx.Response.Body, Encoding.UTF8).ReadToEndAsync();
        
        // Verify that totalAvailability is NOT in the response
        Assert.That(responseText, Does.Not.Contain("totalAvailability"));
    }

    [Test]
    public async Task IncludeTotalAvailability_True_With_No_Products_Does_Not_Call_Inventory()
    {
        // Arrange - setup returns no products
        var ipfs = new InMemoryIpfsStore();
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

        // Act - includeTotalAvailability = true
        await OperatorCatalogEndpoint.Handle(
            "0x1234567890123456789012345678901234567890", 
            100, 0, null, 10, null, 0, true, 
            ctx, routes, svc, mockInventoryClient.Object, CancellationToken.None);

        // Assert
        Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        
        // Verify inventory client was not called (no products to fetch inventory for)
        mockInventoryClient.Verify(
            x => x.FetchInventoryAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), 
            Times.Never);
    }
}
