using System.Net;
using System.Text;
using System.Text.Json;
using Circles.Market.Api.Cart;
using Circles.Market.Api.Fulfillment;
using Microsoft.Extensions.Logging.Abstractions;

namespace Circles.Market.Tests;

public class HttpOrderFulfillmentClientTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? CapturedRequest { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequest = request;
            var json = JsonSerializer.Serialize(new { ok = true });
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(resp);
        }
    }

    private sealed class NamedHttpClientFactory : IHttpClientFactory
    {
        private readonly IDictionary<string, HttpClient> _clients;
        private readonly HttpClient _defaultClient;
        public NamedHttpClientFactory(IDictionary<string, HttpClient> clients)
        {
            _clients = clients;
            _defaultClient = clients.Values.First();
        }
        public HttpClient CreateClient(string name)
        {
            return _clients.TryGetValue(name, out var client) ? client : _defaultClient;
        }
    }

    private sealed class FakeOutboundAuthProvider : Circles.Market.Api.Auth.IOutboundServiceAuthProvider
    {
        private readonly (string header, string key)? _result;
        public FakeOutboundAuthProvider((string header, string key)? result) { _result = result; }
        public Task<(string headerName, string apiKey)?> TryGetHeaderAsync(Uri endpoint, string serviceKind, string? sellerAddress, long chainId, CancellationToken ct = default)
            => Task.FromResult(_result.HasValue ? (ValueTuple<string,string>?) (_result.Value.header, _result.Value.key) : null);
    }

    [Test]
    public async Task FulfillAsync_DoesNotSendAuthorizationHeader()
    {
        var handler = new CapturingHandler();
        var client = new HttpClient(handler);
        // Ensure there is NO default Authorization header on this client
        client.DefaultRequestHeaders.Authorization = null;

        var factory = new NamedHttpClientFactory(new Dictionary<string, HttpClient>
        {
            { "fulfillment", client }
        });

        var sut = new HttpOrderFulfillmentClient(factory, NullLogger<HttpOrderFulfillmentClient>.Instance, new FakeOutboundAuthProvider(null));

        var order = new OrderSnapshot
        {
            OrderNumber = "ord_test",
            PaymentReference = "pay_test",
            Customer = new SchemaOrgPersonId { Id = "eip155:100:0xabc" },
            OrderedItem = new List<OrderItemLine>
            {
                new() { OrderQuantity = 1, OrderedItem = new OrderedItemRef { Sku = "sku1" } }
            }
        };

        var result = await sut.FulfillAsync("https://example.com/fulfill", orderId: "ord_test", paymentReference: "pay_test", order: order, trigger: "manual", ct: CancellationToken.None);

        Assert.That(handler.CapturedRequest, Is.Not.Null, "Request was not captured");
        var req = handler.CapturedRequest!;
        // Assert no Authorization header at all
        Assert.That(req.Headers.Authorization, Is.Null, "Authorization header must not be set");
        Assert.That(req.Headers.Contains("Authorization"), Is.False, "Authorization header must not be present");
        // basic sanity: response parsed
        Assert.That(result.ValueKind, Is.EqualTo(JsonValueKind.Object));
        Assert.That(result.TryGetProperty("ok", out var ok) && ok.GetBoolean(), Is.True);
    }

    [Test]
    public async Task FulfillAsync_SendsConfiguredHeader()
    {
        var handler = new CapturingHandler();
        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Clear();
        var factory = new NamedHttpClientFactory(new Dictionary<string, HttpClient>
        {
            { "fulfillment", client }
        });

        var sut = new HttpOrderFulfillmentClient(factory, NullLogger<HttpOrderFulfillmentClient>.Instance, new FakeOutboundAuthProvider(("X-Circles-Service-Key", "sekret")));

        var order = new OrderSnapshot
        {
            OrderNumber = "ord_test2",
            PaymentReference = "pay_test2",
            Customer = new SchemaOrgPersonId { Id = "eip155:100:0xabc" },
            OrderedItem = new List<OrderItemLine>
            {
                new() { OrderQuantity = 1, OrderedItem = new OrderedItemRef { Sku = "sku1" } }
            }
        };

        // include chainId and seller in path so selector can use them
        var endpoint = "https://example.com/fulfill/100/0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var _ = await sut.FulfillAsync(endpoint, orderId: "ord_test2", paymentReference: "pay_test2", order: order, trigger: "manual", ct: CancellationToken.None);
        var req = handler.CapturedRequest!;
        Assert.That(req, Is.Not.Null);
        Assert.That(req.Headers.TryGetValues("X-Circles-Service-Key", out var values), Is.True);
        Assert.That(values!.Single(), Is.EqualTo("sekret"));
    }
}
