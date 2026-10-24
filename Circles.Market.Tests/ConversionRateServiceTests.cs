using Circles.Market.Adapters.Odoo.Conversion;
using Microsoft.Extensions.Logging;

namespace Circles.Market.Tests;

public class CirclesPricingServiceTests
{
    private readonly ILogger<CirclesPricingService> _log =
        LoggerFactory.Create(b => b.AddDebug()).CreateLogger<CirclesPricingService>();

    /// <summary>
    /// Simulates the metrics-api /pricing response:
    /// {"date":"2026-04-22","scrc_xdai":0.00857,"conv_factor":0.67,"dcrc_xdai":0.0128,"xdai_eur":0.85,"dcrc_eur":0.0109,"source":"balancer_live"}
    /// </summary>
    private const string PricingJson =
        @"{""date"":""2026-04-22"",""scrc_xdai"":0.008574,""conv_factor"":0.67008,""dcrc_xdai"":0.012796,""xdai_eur"":0.85157,""dcrc_eur"":0.010897,""source"":""balancer_live""}";

    [Test]
    public async Task ComputeAsync_ReturnsNull_WhenAmountWeiIsNull()
    {
        var http = new MockHttpMessageHandler(PricingJson);
        var svc = new CirclesPricingService(http.Client, _log);
        var result = await svc.ComputeAsync("ord1", "pay1", null, null);
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task ComputeAsync_ReturnsNull_WhenAmountWeiIsEmpty()
    {
        var http = new MockHttpMessageHandler(PricingJson);
        var svc = new CirclesPricingService(http.Client, _log);
        var result = await svc.ComputeAsync("ord1", "pay1", "", null);
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task ComputeAsync_ReturnsNull_WhenAmountWeiIsInvalid()
    {
        var http = new MockHttpMessageHandler(PricingJson);
        var svc = new CirclesPricingService(http.Client, _log);
        var result = await svc.ComputeAsync("ord1", "pay1", "not-a-number", null);
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task ComputeAsync_ReturnsNull_WhenPricingApiFails()
    {
        var http = new MockHttpMessageHandler("", System.Net.HttpStatusCode.InternalServerError);
        var svc = new CirclesPricingService(http.Client, _log)
        {
            ApiBaseUrl = http.BaseUrl
        };
        var result = await svc.ComputeAsync("ord1", "pay1", "1000000000000000000", "2026-04-22T12:00:00Z");
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task ComputeAsync_ComputesCorrectEurEquivalent()
    {
        // Using Tobias's conversion chain:
        // 1 dCRC = 0.010897 EUR (from dcrc_eur in pricing response)
        // 1 CRC = 0.010897 EUR
        // So 1 CRC → €0.010897
        var http = new MockHttpMessageHandler(PricingJson);
        var svc = new CirclesPricingService(http.Client, _log)
        {
            ApiBaseUrl = http.BaseUrl
        };
        var result = await svc.ComputeAsync("ord1", "pay1", "1000000000000000000", "2026-04-22T12:00:00Z");

        Assert.That(result, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(result!.OrderId, Is.EqualTo("ord1"));
            Assert.That(result.PaymentReference, Is.EqualTo("pay1"));
            Assert.That(result.AmountCrc, Is.EqualTo(1m));                           // 1e18 wei = 1 CRC
            Assert.That(result.ScrToXdaiRate, Is.EqualTo(0.008574m));                // sCRC/xDAI from Balancer
            Assert.That(result.ConversionFactor, Is.EqualTo(0.67008m));              // demurrage factor
            Assert.That(result.DcrcToXdaiRate, Is.EqualTo(0.012796m));              // dCRC/xDAI (with demurrage)
            Assert.That(result.XdaiToEurRate, Is.EqualTo(0.85157m));                // xDAI/EUR
            Assert.That(result.EurEquivalent, Is.EqualTo(0.010897m).Within(0.000001m)); // 1 × dcrc_eur
            Assert.That(result.PriceDate, Is.EqualTo("2026-04-22"));
            Assert.That(result.PricingSource, Is.EqualTo("balancer_live"));
        });
    }

    [Test]
    public async Task ComputeAsync_HandlesLargeWeiAmounts()
    {
        // 500 CRC = 500 × dcrc_eur = 500 × 0.010897 = 5.4485 EUR
        var http = new MockHttpMessageHandler(PricingJson);
        var svc = new CirclesPricingService(http.Client, _log)
        {
            ApiBaseUrl = http.BaseUrl
        };

        var fiveHundredCrcInWei = "500000000000000000000"; // 500 × 1e18
        var result = await svc.ComputeAsync("ord2", "pay2", fiveHundredCrcInWei, "2026-04-22T00:00:00Z");

        Assert.That(result, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(result!.AmountCrc, Is.EqualTo(500m));
            Assert.That(result.EurEquivalent, Is.EqualTo(5.4485m).Within(0.0001m)); // 500 × 0.010897
        });
    }

    [Test]
    public async Task ComputeAsync_UsesToday_WhenNoTimestampProvided()
    {
        var http = new MockHttpMessageHandler(PricingJson);
        var svc = new CirclesPricingService(http.Client, _log)
        {
            ApiBaseUrl = http.BaseUrl
        };
        var result = await svc.ComputeAsync("ord3", "pay3", "1000000000000000000", null);

        Assert.That(result, Is.Not.Null);
        // PriceDate comes from the API response, not from our date extraction
        Assert.That(result!.PriceDate, Is.EqualTo("2026-04-22"));
    }

    [Test]
    public async Task ComputeAsync_NeverThrows_EvenWhenApiThrows()
    {
        var http = new ThrowingHttpMessageHandler();
        var svc = new CirclesPricingService(http.Client, _log)
        {
            ApiBaseUrl = "http://mock-test"
        };
        var result = await svc.ComputeAsync("ord5", "pay5", "1000000000000000000", null);

        // Should return null, not throw
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task ComputeAsync_IncludesAllDemurrageFields()
    {
        // Verify the full conversion chain is captured:
        // sCRC/xDAI → ÷ conv_factor → dCRC/xDAI → × xDAI/EUR → dCRC/EUR
        var json = @"{""date"":""2026-04-22"",""scrc_xdai"":0.00857,""conv_factor"":0.67,""dcrc_xdai"":0.0128,""xdai_eur"":0.85,""dcrc_eur"":0.01088,""source"":""balancer_live""}";
        var http = new MockHttpMessageHandler(json);
        var svc = new CirclesPricingService(http.Client, _log)
        {
            ApiBaseUrl = http.BaseUrl
        };

        // 100 CRC
        var result = await svc.ComputeAsync("ord6", "pay6", "100000000000000000000", "2026-04-22T12:00:00Z");

        Assert.That(result, Is.Not.Null);
        Assert.Multiple(() =>
        {
            // Verify chain: 0.00857 / 0.67 ≈ 0.0128 (dcrc_xdai)
            Assert.That(result!.ScrToXdaiRate, Is.EqualTo(0.00857m));
            Assert.That(result.ConversionFactor, Is.EqualTo(0.67m));
            Assert.That(result.DcrcToXdaiRate, Is.EqualTo(0.0128m));
            Assert.That(result.XdaiToEurRate, Is.EqualTo(0.85m));
            // 100 × 0.01088 = 1.088 EUR
            Assert.That(result.EurEquivalent, Is.EqualTo(1.088m).Within(0.0001m));
        });
    }
}

/// <summary>
/// Minimal mock HttpMessageHandler that returns a fixed response.
/// </summary>
internal sealed class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly string _response;
    private readonly System.Net.HttpStatusCode _statusCode;

    public HttpClient Client { get; }
    public string BaseUrl { get; } = "http://mock-test";

    public MockHttpMessageHandler(string response, System.Net.HttpStatusCode statusCode = System.Net.HttpStatusCode.OK)
    {
        _response = response;
        _statusCode = statusCode;
        Client = new HttpClient(this) { BaseAddress = new Uri(BaseUrl) };
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var resp = new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_response, System.Text.Encoding.UTF8, "application/json")
        };
        return Task.FromResult(resp);
    }
}

/// <summary>
/// HttpMessageHandler that always throws, for testing resilience.
/// </summary>
internal sealed class ThrowingHttpMessageHandler : HttpMessageHandler
{
    public HttpClient Client { get; } = new HttpClient();

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
        => throw new HttpRequestException("network error");
}