using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

    [Test]
    public async Task CreatePartnerAsync_ShouldIncludeCountryId_WhenProvided()
    {
        string? capturedBody = null;

        var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
            {
                capturedBody = request.Content is null ? null : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, result = 99 })
                };
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

        var partnerId = await client.CreatePartnerAsync(new PartnerCreateDto
        {
            Name = "Alice",
            Email = "alice@example.com",
            Phone = "+491234",
            Street = "Main St 1",
            City = "Berlin",
            Zip = "10115",
            CountryId = 56
        });

        Assert.That(partnerId, Is.EqualTo(99));
        Assert.That(capturedBody, Is.Not.Null.And.Not.Empty);

        using var doc = JsonDocument.Parse(capturedBody!);
        var args = doc.RootElement.GetProperty("params").GetProperty("args");
        var createVals = args[5][0];
        Assert.That(createVals.GetProperty("country_id").GetInt32(), Is.EqualTo(56));
    }

    [Test]
    public async Task ResolveCountryIdAsync_ShouldResolveByCode_First()
    {
        int call = 0;
        var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                call++;
                return new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = JsonContent.Create(new
                    {
                        jsonrpc = "2.0",
                        id = 1,
                        result = call == 1
                            ? new[] { new { id = 56, name = "Germany", code = "DE" } }
                            : Array.Empty<object>()
                    })
                };
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

        var countryId = await client.ResolveCountryIdAsync("de");
        Assert.That(countryId, Is.EqualTo(56));
    }

    [Test]
    public async Task CreateSaleOrderAsync_ShouldStampClientOrderRef_WhenProvided()
    {
        string? capturedBody = null;
        var client = ClientCapturing(b => capturedBody = b, result: new[] { 40 });

        await client.CreateSaleOrderAsync(new SaleOrderCreateDto
        {
            PartnerId = 9,
            Lines = new List<SaleOrderLineDto> { new() { ProductId = 3, Quantity = 1 } },
            ClientOrderRef = "ord_ABC123"
        });

        using var doc = JsonDocument.Parse(capturedBody!);
        var createVals = doc.RootElement.GetProperty("params").GetProperty("args")[5][0];
        Assert.That(createVals.GetProperty("client_order_ref").GetString(), Is.EqualTo("ord_ABC123"));
    }

    [Test]
    public async Task CreateSaleOrderAsync_ShouldOmitClientOrderRef_WhenNotProvided()
    {
        string? capturedBody = null;
        var client = ClientCapturing(b => capturedBody = b, result: new[] { 40 });

        await client.CreateSaleOrderAsync(new SaleOrderCreateDto
        {
            PartnerId = 9,
            Lines = new List<SaleOrderLineDto> { new() { ProductId = 3, Quantity = 1 } }
        });

        using var doc = JsonDocument.Parse(capturedBody!);
        var createVals = doc.RootElement.GetProperty("params").GetProperty("args")[5][0];
        Assert.That(createVals.TryGetProperty("client_order_ref", out _), Is.False);
    }

    [Test]
    public async Task FindSaleOrderByClientRefAsync_ReturnsIdAndName_WhenMatchExists()
    {
        var client = ClientReturning(new { jsonrpc = "2.0", id = 1, result = new[] { new { id = 77, name = "S00077" } } });

        var found = await client.FindSaleOrderByClientRefAsync("ord_ABC123");

        Assert.That(found, Is.Not.Null);
        Assert.That(found!.Value.Id, Is.EqualTo(77));
        Assert.That(found.Value.Name, Is.EqualTo("S00077"));
    }

    [Test]
    public async Task FindSaleOrderByClientRefAsync_ReturnsNull_WhenNoMatch()
    {
        var client = ClientReturning(new { jsonrpc = "2.0", id = 1, result = Array.Empty<object>() });

        var found = await client.FindSaleOrderByClientRefAsync("ord_MISSING");

        Assert.That(found, Is.Null);
    }

    [Test]
    public async Task FindSaleOrderByClientRefAsync_ReturnsNull_ForBlankRef_WithoutHttpCall()
    {
        // MockBehavior.Strict => any HTTP call throws; a blank ref must short-circuit before that.
        var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        var client = new OdooClient(new HttpClient(handlerMock.Object), Settings(), NullLogger<OdooClient>.Instance);

        Assert.That(await client.FindSaleOrderByClientRefAsync("  "), Is.Null);
    }

    // --- helpers -------------------------------------------------------------

    private static OdooSettings Settings() => new()
    {
        BaseUrl = "https://odoo.example.com",
        Db = "testdb",
        UserId = 1,
        Key = "password"
    };

    private static OdooClient ClientReturning(object responseBody)
    {
        var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = JsonContent.Create(responseBody)
            });
        var client = new OdooClient(new HttpClient(handlerMock.Object), Settings(), NullLogger<OdooClient>.Instance);
        client.UpdateBaseAddressAsync(CancellationToken.None).GetAwaiter().GetResult();
        return client;
    }

    private static OdooClient ClientCapturing(Action<string?> onBody, object result)
    {
        var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
            {
                onBody(request.Content is null ? null : request.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                return new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, result })
                };
            });
        var client = new OdooClient(new HttpClient(handlerMock.Object), Settings(), NullLogger<OdooClient>.Instance);
        client.UpdateBaseAddressAsync(CancellationToken.None).GetAwaiter().GetResult();
        return client;
    }
}
