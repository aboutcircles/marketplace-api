using System.Net;
using System.Text;
using System.Text.Json;
using Circles.Market.Adapters.WooCommerce;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using NUnit.Framework;

namespace Circles.Market.Tests;

[TestFixture]
public class WooCommerceClientTests
{
    private static WooCommerceSettings MakeSettings() => new()
    {
        BaseUrl = "https://example.com",
        ConsumerKey = "ck_test",
        ConsumerSecret = "cs_test"
    };

    private static (WooCommerceClient client, Mock<HttpMessageHandler> handler) CreateClient()
    {
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        var http = new HttpClient(handler.Object);
        var settings = MakeSettings();
        var logger = new Mock<ILogger<WooCommerceClient>>().Object;
        var client = new WooCommerceClient(http, settings, logger);
        return (client, handler);
    }

    private static void SetupHandler(Mock<HttpMessageHandler> handler, HttpMethod method, string url,
        HttpStatusCode statusCode, string responseBody)
    {
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r =>
                    r.Method == method &&
                    r.RequestUri!.ToString().StartsWith(url)),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
    }

    // ── GetProductBySkuAsync ──────────────────────────────────────────────

    [Test]
    public async Task GetProductBySkuAsync_ReturnsProduct_WhenFound()
    {
        var (client, handler) = CreateClient();
        SetupHandler(handler, HttpMethod.Get, "https://example.com/wp-json/wc/v3/products",
            HttpStatusCode.OK, """[{"id":42,"name":"Test","sku":"SKU1","price":"10.00"}]""");

        var result = await client.GetProductBySkuAsync("SKU1", CancellationToken.None);
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Id, Is.EqualTo(42));
        Assert.That(result.Sku, Is.EqualTo("SKU1"));
    }

    [Test]
    public async Task GetProductBySkuAsync_ReturnsNull_WhenNotFound()
    {
        var (client, handler) = CreateClient();
        SetupHandler(handler, HttpMethod.Get, "https://example.com/wp-json/wc/v3/products",
            HttpStatusCode.OK, "[]");

        var result = await client.GetProductBySkuAsync("NOSKU", CancellationToken.None);
        Assert.That(result, Is.Null);
    }

    // ── GetProductByIdAsync ───────────────────────────────────────────────

    [Test]
    public async Task GetProductByIdAsync_ReturnsProduct_WhenFound()
    {
        var (client, handler) = CreateClient();
        SetupHandler(handler, HttpMethod.Get, "https://example.com/wp-json/wc/v3/products/42",
            HttpStatusCode.OK, """{"id":42,"name":"Test Product","sku":"SKU42","price":"15.00"}""");

        var result = await client.GetProductByIdAsync(42, CancellationToken.None);
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Id, Is.EqualTo(42));
        Assert.That(result.Name, Is.EqualTo("Test Product"));
    }

    [Test]
    public async Task GetProductByIdAsync_ReturnsNull_WhenNotFound()
    {
        var (client, handler) = CreateClient();
        SetupHandler(handler, HttpMethod.Get, "https://example.com/wp-json/wc/v3/products/999",
            HttpStatusCode.NotFound, """{"code":"woocommerce_rest_product_invalid_id","message":"Invalid product ID.","data":{"status":404}}""");

        var result = await client.GetProductByIdAsync(999, CancellationToken.None);
        Assert.That(result, Is.Null);
    }

    // ── ListProductsAsync ─────────────────────────────────────────────────

    [Test]
    public async Task ListProductsAsync_ReturnsAllProducts()
    {
        var (client, handler) = CreateClient();
        SetupHandler(handler, HttpMethod.Get, "https://example.com/wp-json/wc/v3/products",
            HttpStatusCode.OK,
            """[{"id":1,"name":"A","sku":"S1"},{"id":2,"name":"B","sku":"S2"}]""");

        var result = await client.ListProductsAsync();
        Assert.That(result, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task ListProductsAsync_FiltersBySku()
    {
        var (client, handler) = CreateClient();
        SetupHandler(handler, HttpMethod.Get, "https://example.com/wp-json/wc/v3/products",
            HttpStatusCode.OK, """[{"id":1,"name":"A","sku":"S1"}]""");

        var result = await client.ListProductsAsync(sku: "S1");
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Sku, Is.EqualTo("S1"));
    }

    // ── CreateOrderAsync ──────────────────────────────────────────────────

    [Test]
    public async Task CreateOrderAsync_ReturnsOrder()
    {
        var (client, handler) = CreateClient();
        SetupHandler(handler, HttpMethod.Post, "https://example.com/wp-json/wc/v3/orders",
            HttpStatusCode.Created,
            """{"id":100,"number":"WC-100","status":"pending","total":"20.00"}""");

        var request = new WcCreateOrderRequest
        {
            LineItems = new List<WcLineItem>
            {
                new() { ProductId = 42, Quantity = 2 }
            },
            MetaData = new List<WcMetaData>
            {
                new() { Key = "payment_reference", Value = "0xabc" }
            }
        };

        var result = await client.CreateOrderAsync(request, CancellationToken.None);
        Assert.That(result.Id, Is.EqualTo(100));
        Assert.That(result.Status, Is.EqualTo("pending"));
    }

    [Test]
    public void CreateOrderAsync_Throws_OnError()
    {
        var (client, handler) = CreateClient();
        SetupHandler(handler, HttpMethod.Post, "https://example.com/wp-json/wc/v3/orders",
            HttpStatusCode.BadRequest,
            """{"code":"woocommerce_rest_invalid_product","message":"Product invalid.","data":{"status":400}}""");

        var request = new WcCreateOrderRequest
        {
            LineItems = new List<WcLineItem>()
        };

        var ex = Assert.ThrowsAsync<WooCommerceApiException>(async () =>
            await client.CreateOrderAsync(request, CancellationToken.None));
        Assert.That(ex!.Code, Does.Contain("woocommerce_rest_invalid_product"));
    }

    // ── GetOrderAsync ─────────────────────────────────────────────────────

    [Test]
    public async Task GetOrderAsync_ReturnsOrder_WhenFound()
    {
        var (client, handler) = CreateClient();
        SetupHandler(handler, HttpMethod.Get, "https://example.com/wp-json/wc/v3/orders/100",
            HttpStatusCode.OK,
            """{"id":100,"number":"WC-100","status":"processing","total":"20.00"}""");

        var result = await client.GetOrderAsync(100, CancellationToken.None);
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Id, Is.EqualTo(100));
        Assert.That(result.Status, Is.EqualTo("processing"));
    }

    [Test]
    public void GetOrderAsync_Throws_WhenNotFound()
    {
        var (client, handler) = CreateClient();
        SetupHandler(handler, HttpMethod.Get, "https://example.com/wp-json/wc/v3/orders/999",
            HttpStatusCode.NotFound, """{"code":"404","message":"Not found"}""");

        var ex = Assert.ThrowsAsync<WooCommerceApiException>(async () =>
            await client.GetOrderAsync(999, CancellationToken.None));
        Assert.That(ex!.Code, Is.EqualTo("404"));
    }

    // ── GetOrCreateCustomerAsync ──────────────────────────────────────────

    [Test]
    public async Task GetOrCreateCustomerAsync_ReturnsExistingId()
    {
        var (client, handler) = CreateClient();
        SetupHandler(handler, HttpMethod.Get, "https://example.com/wp-json/wc/v3/customers",
            HttpStatusCode.OK,
            """[{"id":5,"email":"a@b.com","first_name":"A","last_name":"B"}]""");

        var result = await client.GetOrCreateCustomerAsync("a@b.com", "A", "B", null, null, CancellationToken.None);
        Assert.That(result, Is.EqualTo(5));
    }

    [Test]
    public async Task GetOrCreateCustomerAsync_CreatesNew_WhenNotExisting()
    {
        var (client, handler) = CreateClient();

        // First call: search returns empty
        var seq = handler.Protected().SetupSequence<Task<HttpResponseMessage>>("SendAsync",
            ItExpr.Is<HttpRequestMessage>(r => r.Method == HttpMethod.Get),
            ItExpr.IsAny<CancellationToken>());
        seq.ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", Encoding.UTF8, "application/json")
        });

        // We need a separate setup for POST
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.Method == HttpMethod.Post),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("""{"id":10,"email":"x@y.com"}""", Encoding.UTF8, "application/json")
            });

        var result = await client.GetOrCreateCustomerAsync("x@y.com", "X", "Y", null, null, CancellationToken.None);
        Assert.That(result, Is.EqualTo(10));
    }

    // ── Auth query parameters ─────────────────────────────────────────────

    [Test]
    public async Task Client_SendsBasicAuth_WithCredentials()
    {
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        string? authHeader = null;
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => true),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) =>
            {
                authHeader = req.Headers.Authorization?.ToString();
            })
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            });

        var http = new HttpClient(handler.Object);
        var settings = new WooCommerceSettings
        {
            BaseUrl = "https://example.com",
            ConsumerKey = "ck_mykey",
            ConsumerSecret = "cs_mysecret"
        };
        var client = new WooCommerceClient(http, settings, new Mock<ILogger<WooCommerceClient>>().Object);

        await client.ListProductsAsync();
        Assert.That(authHeader, Does.Contain("Basic"));
    }

    // ── Null / edge cases ─────────────────────────────────────────────────

    [Test]
    public void Settings_ApiBaseUrl_ConstructsCorrectEndpoint()
    {
        var settings = new WooCommerceSettings
        {
            BaseUrl = "https://myshop.com/",
            ConsumerKey = "ck",
            ConsumerSecret = "cs"
        };
        Assert.That(settings.ApiBaseUrl, Is.EqualTo("https://myshop.com/wp-json/wc/v3/"));
    }

    [Test]
    public void Settings_DefaultTimeout_Is30Seconds()
    {
        var settings = new WooCommerceSettings
        {
            BaseUrl = "https://example.com",
            ConsumerKey = "ck",
            ConsumerSecret = "cs"
        };
        Assert.That(settings.TimeoutMs, Is.EqualTo(30000));
    }

    [Test]
    public void WcProductDto_Defaults_AreCorrect()
    {
        var dto = new WcProductDto();
        Assert.That(dto.Name, Is.EqualTo(string.Empty));
        Assert.That(dto.OnSale, Is.False);
        Assert.That(dto.ManageStock, Is.False);
        Assert.That(dto.Purchasable, Is.False);
    }

    [Test]
    public void WcCreateOrderRequest_Defaults_AreCorrect()
    {
        var req = new WcCreateOrderRequest();
        Assert.That(req.Status, Is.EqualTo("pending"));
        Assert.That(req.Currency, Is.EqualTo("EUR"));
        Assert.That(req.LineItems, Is.Empty);
    }

    [Test]
    public void WooCommerceFulfillmentResult_Defaults_AreCorrect()
    {
        var result = new WooCommerceFulfillmentResult();
        Assert.That(result.Outcome, Is.Empty);
        Assert.That(result.Errors, Is.Null);
    }

}