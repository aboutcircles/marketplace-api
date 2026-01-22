using System.Net;
using System.Net.Http.Json;
using Circles.Market.Adapters.Odoo;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;

namespace Circles.Market.Tests;

[TestFixture]
public class OdooClientTests
{
    [Test]
    public async Task UpdateBaseAddressAsync_ShouldNotThrow_EvenIfRequestAlreadyMade()
    {
        // Arrange
        var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);

        var httpClient = new HttpClient(handlerMock.Object);
        var settings = new OdooSettings
        {
            BaseUrl = "https://odoo.example.com",
            Db = "testdb",
            UserId = 1,
            Key = "password"
        };
        var logger = NullLogger<OdooClient>.Instance;

        var client = new OdooClient(httpClient, settings, logger);

        // Act & Assert

        // First call:
        await client.UpdateBaseAddressAsync(CancellationToken.None);

        // Second call:
        Assert.DoesNotThrowAsync(async () => await client.UpdateBaseAddressAsync(CancellationToken.None));

        Assert.That(httpClient.BaseAddress!.ToString(), Is.EqualTo("https://odoo.example.com/jsonrpc"));
    }

    [Test]
    public async Task ExecuteKwAsync_OnRpcError_ShouldLogData()
    {
        // Arrange
        var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);

        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(() => new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = JsonContent.Create(new
                {
                    jsonrpc = "2.0",
                    id = 1,
                    error = new
                    {
                        code = 100,
                        message = "Some Odoo Error",
                        data = new { debug = "detailed info" }
                    }
                })
            });

        var httpClient = new HttpClient(handlerMock.Object);
        var settings = new OdooSettings
        {
            BaseUrl = "https://odoo.example.com",
            Db = "testdb",
            UserId = 1,
            Key = "password"
        };

        // We want to capture logs, but for now let's just make sure it doesn't crash when trying to log 'data'
        var client = new OdooClient(httpClient, settings, NullLogger<OdooClient>.Instance);

        // Act & Assert
        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.SearchReadAsync<object>("res.partner", Array.Empty<object>(), Array.Empty<string>()));

        Assert.That(ex!.Message, Contains.Substring("Odoo error 100: Some Odoo Error"));
    }

    [Test]
    public async Task CreateSaleOrderAsync_ShouldHandleArrayResult()
    {
        // Arrange
        var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);

        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(() => new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = JsonContent.Create(new
                {
                    jsonrpc = "2.0",
                    id = 1,
                    result = new[] { 40 }
                })
            });

        var httpClient = new HttpClient(handlerMock.Object);
        var settings = new OdooSettings
        {
            BaseUrl = "https://odoo.example.com",
            Db = "testdb",
            UserId = 1,
            Key = "password"
        };

        var client = new OdooClient(httpClient, settings, NullLogger<OdooClient>.Instance);
        await client.UpdateBaseAddressAsync(CancellationToken.None);

        var order = new SaleOrderCreateDto
        {
            PartnerId = 9,
            Lines = new List<SaleOrderLineDto>
            {
                new() { ProductId = 3, Quantity = 1 }
            }
        };

        // Act
        int orderId = await client.CreateSaleOrderAsync(order);

        // Assert
        Assert.That(orderId, Is.EqualTo(40));
    }

    [Test]
    public async Task CreateSaleOrderAsync_ShouldHandleScalarResult()
    {
        // Arrange
        var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);

        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(() => new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = JsonContent.Create(new
                {
                    jsonrpc = "2.0",
                    id = 1,
                    result = 41
                })
            });

        var httpClient = new HttpClient(handlerMock.Object);
        var settings = new OdooSettings
        {
            BaseUrl = "https://odoo.example.com",
            Db = "testdb",
            UserId = 1,
            Key = "password"
        };

        var client = new OdooClient(httpClient, settings, NullLogger<OdooClient>.Instance);
        await client.UpdateBaseAddressAsync(CancellationToken.None);

        var order = new SaleOrderCreateDto
        {
            PartnerId = 9,
            Lines = new List<SaleOrderLineDto>
            {
                new() { ProductId = 3, Quantity = 1 }
            }
        };

        // Act
        int orderId = await client.CreateSaleOrderAsync(order);

        // Assert
        Assert.That(orderId, Is.EqualTo(41));
    }

    [Test]
    public async Task GetOperationDetailsByOriginAsync_ShouldHandleOdooFalseValues()
    {
        // Arrange
        var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);

        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(() => new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = JsonContent.Create(new
                {
                    jsonrpc = "2.0",
                    id = 1,
                    result = new[]
                    {
                        new
                        {
                            id = 5,
                            carrier_id = false,
                            carrier_tracking_ref = false
                        }
                    }
                })
            });

        var httpClient = new HttpClient(handlerMock.Object);
        var settings = new OdooSettings
        {
            BaseUrl = "https://odoo.example.com",
            Db = "testdb",
            UserId = 1,
            Key = "password"
        };

        var client = new OdooClient(httpClient, settings, NullLogger<OdooClient>.Instance);
        await client.UpdateBaseAddressAsync(CancellationToken.None);

        // Act
        var result = await client.GetOperationDetailsByOriginAsync("S0001");

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Id, Is.EqualTo(5));
        Assert.That(result.CarrierId, Is.Null);
        Assert.That(result.CarrierTrackingRef, Is.Null);
    }
}
