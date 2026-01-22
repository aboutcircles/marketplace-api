using System.Net;
using Circles.Market.Api.Outbound;

namespace Circles.Market.Tests;

[TestFixture]
public class OutboundGuardsTests
{
    private sealed class RedirectHandler : HttpMessageHandler
    {
        private readonly List<(HttpStatusCode Status, Uri? Location)> _responses;
        private int _callCount = 0;
        public List<HttpRequestMessage> CapturedRequests { get; } = new();

        public RedirectHandler(List<(HttpStatusCode Status, Uri? Location)> responses)
        {
            _responses = responses;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequests.Add(request);
            if (_callCount >= _responses.Count)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
            }

            var (status, location) = _responses[_callCount++];
            var resp = new HttpResponseMessage(status);
            if (location != null)
            {
                resp.Headers.Location = location;
            }
            if (status == HttpStatusCode.OK)
            {
                resp.Content = new StringContent("{}");
            }
            return Task.FromResult(resp);
        }
    }

    [Test]
    public async Task SendWithRedirectsAsync_PostTo303To302_ResultsInGetForHops()
    {
        // initial POST -> 303 -> 302 -> 200
        var responses = new List<(HttpStatusCode Status, Uri? Location)>
        {
            (HttpStatusCode.SeeOther, new Uri("https://example.com/r1")),
            (HttpStatusCode.Found, new Uri("https://example.com/r2")),
            (HttpStatusCode.OK, null)
        };
        var handler = new RedirectHandler(responses);
        using var client = new HttpClient(handler);

        var initialRequest = new HttpRequestMessage(HttpMethod.Post, "https://example.com/start")
        {
            Content = new StringContent("{\"test\":1}", System.Text.Encoding.UTF8, "application/json")
        };

        var response = await OutboundGuards.SendWithRedirectsAsync(
            client,
            initialRequest,
            3,
            uri => new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent("{\"test\":1}") },
            CancellationToken.None);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(handler.CapturedRequests.Count, Is.EqualTo(3));

        // 1st request: POST
        Assert.That(handler.CapturedRequests[0].Method, Is.EqualTo(HttpMethod.Post));
        Assert.That(handler.CapturedRequests[0].RequestUri!.ToString(), Is.EqualTo("https://example.com/start"));

        // 2nd request: 303 SeeOther makes it GET
        Assert.That(handler.CapturedRequests[1].Method, Is.EqualTo(HttpMethod.Get));
        Assert.That(handler.CapturedRequests[1].RequestUri!.ToString(), Is.EqualTo("https://example.com/r1"));
        Assert.That(handler.CapturedRequests[1].Content, Is.Null);

        // 3rd request: even if rebuildRequest returns POST, effectiveMethod (GET) should stick
        Assert.That(handler.CapturedRequests[2].Method, Is.EqualTo(HttpMethod.Get));
        Assert.That(handler.CapturedRequests[2].RequestUri!.ToString(), Is.EqualTo("https://example.com/r2"));
        Assert.That(handler.CapturedRequests[2].Content, Is.Null);
    }

    [Test]
    public async Task SendWithRedirectsAsync_PostTo307_PreservesPost()
    {
        var responses = new List<(HttpStatusCode Status, Uri? Location)>
        {
            (HttpStatusCode.TemporaryRedirect, new Uri("https://example.com/r1")),
            (HttpStatusCode.OK, null)
        };
        var handler = new RedirectHandler(responses);
        using var client = new HttpClient(handler);

        var initialRequest = new HttpRequestMessage(HttpMethod.Post, "https://example.com/start")
        {
            Content = new StringContent("{\"test\":1}")
        };

        var response = await OutboundGuards.SendWithRedirectsAsync(
            client,
            initialRequest,
            3,
            uri => new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent("{\"test\":1}") },
            CancellationToken.None);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(handler.CapturedRequests.Count, Is.EqualTo(2));
        Assert.That(handler.CapturedRequests[0].Method, Is.EqualTo(HttpMethod.Post));
        Assert.That(handler.CapturedRequests[1].Method, Is.EqualTo(HttpMethod.Post));
        Assert.That(handler.CapturedRequests[1].Content, Is.Not.Null);
    }

    [Test]
    public async Task SendWithRedirectsAsync_RelativeRedirect_ResolvesCorrectly()
    {
        var responses = new List<(HttpStatusCode Status, Uri? Location)>
        {
            (HttpStatusCode.Found, new Uri("/r1", UriKind.Relative)),
            (HttpStatusCode.OK, null)
        };
        var handler = new RedirectHandler(responses);
        using var client = new HttpClient(handler);

        var initialRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com/start/page");
        var response = await OutboundGuards.SendWithRedirectsAsync(
            client,
            initialRequest,
            3,
            uri => new HttpRequestMessage(HttpMethod.Get, uri),
            CancellationToken.None);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(handler.CapturedRequests.Count, Is.EqualTo(2));
        Assert.That(handler.CapturedRequests[1].RequestUri!.ToString(), Is.EqualTo("https://example.com/r1"));
    }

    [Test]
    public async Task ReadWithLimitAsync_ConcurrentProbing_IsSafe()
    {
        var content = new StringContent("123");
        var tasks = new List<Task<byte[]>>();
        for (int i = 0; i < 50; i++)
        {
            tasks.Add(OutboundGuards.ReadWithLimitAsync(content, 2, CancellationToken.None));
        }

        foreach (var t in tasks)
        {
            Assert.ThrowsAsync<HttpRequestException>(async () => await t);
        }
    }
}
