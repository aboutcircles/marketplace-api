using System.Net;
using Circles.Market.Adapters.WooCommerce;
using Microsoft.Extensions.Logging.Abstractions;

namespace Circles.Market.Tests;

/// <summary>
/// Pre-create readback behavior of <see cref="WooCommerceClient.FindOrderByPaymentReferenceAsync"/>:
/// the guard that prevents double-shipping when a prior fulfillment attempt failed after
/// WooCommerce committed the order. Null is only a valid answer after a COMPLETE scan of the
/// window — pagination must continue through full pages, a truncated scan must throw
/// (fail closed), and API failures must propagate rather than degrade to "not found".
/// </summary>
[TestFixture]
public class WooCommerceClientOrderLookupTests
{
    private const string Ref = "0xabc123def456";
    private static readonly DateTimeOffset Window = DateTimeOffset.Parse("2026-06-01T00:00:00Z");

    private sealed class RoutingHandler : HttpMessageHandler
    {
        public readonly List<string> Requests = new();
        public Func<string, HttpResponseMessage> Respond = _ => Json("[]");

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string pathAndQuery = request.RequestUri!.PathAndQuery;
            Requests.Add(pathAndQuery);
            return Task.FromResult(Respond(pathAndQuery));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static string Order(int id, string? metaKey = null, string metaValueJson = "null") =>
        $@"{{""id"":{id},""number"":""{id}"",""status"":""processing"",""meta_data"":[" +
        (metaKey is null ? "" : $@"{{""key"":""{metaKey}"",""value"":{metaValueJson}}}") +
        "]}";

    private static string FullPage(int startId) =>
        "[" + string.Join(",", Enumerable.Range(startId, 100).Select(i => Order(i))) + "]";

    private static int PageOf(string url)
    {
        var match = System.Text.RegularExpressions.Regex.Match(url, @"[?&]page=(\d+)");
        return int.Parse(match.Groups[1].Value);
    }

    private RoutingHandler _handler = null!;
    private WooCommerceClient _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _handler = new RoutingHandler();
        _sut = new WooCommerceClient(
            new HttpClient(_handler),
            new WooCommerceSettings
            {
                BaseUrl = "https://shop.test",
                ConsumerKey = "ck_test",
                ConsumerSecret = "cs_test"
            },
            NullLogger<WooCommerceClient>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _sut.Dispose();
        _handler.Dispose();
    }

    private Task<WcOrderDto?> Find() =>
        _sut.FindOrderByPaymentReferenceAsync(Ref, Window, CancellationToken.None);

    [Test]
    public async Task MatchOnFirstPage_ReturnsOrder()
    {
        _handler.Respond = _ => Json($"[{Order(7)},{Order(42, "circles_payment_reference", $@"""{Ref}""")}]");

        var order = await Find();

        Assert.That(order, Is.Not.Null);
        Assert.That(order!.Id, Is.EqualTo(42));
        Assert.That(_handler.Requests, Has.Count.EqualTo(1));
        Assert.That(_handler.Requests[0], Does.Contain("after=").And.Contain("dates_are_gmt=true"),
            "scan must be windowed and use GMT dates");
    }

    [Test]
    public async Task MatchOnSecondPage_ContinuesPastFullFirstPage()
    {
        _handler.Respond = url => PageOf(url) == 1
            ? Json(FullPage(1000))
            : Json($"[{Order(43, "circles_payment_reference", $@"""{Ref}""")}]");

        var order = await Find();

        Assert.That(order, Is.Not.Null);
        Assert.That(order!.Id, Is.EqualTo(43));
        Assert.That(_handler.Requests.Any(r => r.Contains("page=2")), Is.True,
            "a full page must trigger the next page");
    }

    [Test]
    public async Task ShortPageWithoutMatch_ReturnsNull_ProvenAbsent()
    {
        _handler.Respond = _ => Json($"[{Order(1)},{Order(2)}]");

        var order = await Find();

        Assert.That(order, Is.Null);
        Assert.That(_handler.Requests, Has.Count.EqualTo(1), "short page ends the scan");
    }

    [Test]
    public void AllPagesFull_NoMatch_ThrowsInconclusive_FailClosed()
    {
        // "Not found" after a truncated scan is unproven — the guard must refuse to
        // let the caller create a potential duplicate.
        _handler.Respond = url => Json(FullPage(PageOf(url) * 1000));

        var ex = Assert.ThrowsAsync<WooCommerceApiException>(() => Find());

        Assert.That(ex!.Code, Is.EqualTo("wc_lookup_inconclusive"));
        Assert.That(_handler.Requests, Has.Count.EqualTo(10), "scan is bounded at maxScanPages");
    }

    [Test]
    public void HttpClientTimeout_ThrowsTimeout_FailClosed()
    {
        _handler.Respond = _ => throw new TaskCanceledException("timeout", new TimeoutException());

        var ex = Assert.ThrowsAsync<WooCommerceApiException>(() => Find());

        Assert.That(ex!.Code, Is.EqualTo("wc_api_timeout"));
    }

    [Test]
    public void ApiError_Throws_FailClosed()
    {
        _handler.Respond = _ => Json(@"{""code"":""internal"",""message"":""boom""}", HttpStatusCode.InternalServerError);

        Assert.ThrowsAsync<WooCommerceApiException>(() => Find());
    }

    [Test]
    public async Task NonStringMetaValues_AreTolerated()
    {
        string arrayMeta = Order(5, "circles_payment_reference", @"[""not"",""a"",""string""]");
        string objectMeta = Order(6, "some_plugin_meta", @"{""nested"":true}");
        string matching = Order(44, "circles_payment_reference", $@"""{Ref}""");
        _handler.Respond = _ => Json($"[{arrayMeta},{objectMeta},{matching}]");

        var order = await Find();

        Assert.That(order, Is.Not.Null);
        Assert.That(order!.Id, Is.EqualTo(44));
    }

    [Test]
    public async Task DifferentReference_DoesNotMatch()
    {
        _handler.Respond = _ => Json($"[{Order(9, "circles_payment_reference", @"""0xother""")}]");

        var order = await Find();

        Assert.That(order, Is.Null);
    }
}
