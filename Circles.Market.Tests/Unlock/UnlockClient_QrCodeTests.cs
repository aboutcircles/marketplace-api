using System.Net;
using System.Numerics;
using System.Reflection;
using Circles.Market.Adapters.Unlock;
using Microsoft.Extensions.Logging.Abstractions;

namespace Circles.Market.Tests.Unlock;

[TestFixture]
public sealed class UnlockClient_QrCodeTests
{
    [Test]
    public async Task GetTicketQrCodeDataUrlAsync_UsesLockAndKeyPath()
    {
        var handler = new RecordingHandler(req =>
        {
            Assert.That(req.RequestUri, Is.Not.Null);
            var path = req.RequestUri!.AbsolutePath;

            if (path == "/v2/api/ticket/100/lock/0xabc/key/2")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}")
                };
            }

            Assert.That(path, Is.EqualTo("/v2/api/ticket/100/lock/0xabc/key/2/qr"));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
            };
        });

        var client = CreateClient(handler);
        var mapping = new UnlockMappingEntry
        {
            ChainId = 100,
            LockAddress = "0xabc",
            LocksmithBase = "https://locksmith.example/"
        };

        var dataUrl = await client.GetTicketQrCodeDataUrlAsync(mapping, new BigInteger(2), CancellationToken.None);

        Assert.That(dataUrl, Does.StartWith("data:image/png;base64,"));
        var paths = handler.Requests.Select(r => r.RequestUri!.AbsolutePath).ToList();
        Assert.That(paths, Does.Contain("/v2/api/ticket/100/lock/0xabc/key/2"));
        Assert.That(paths, Does.Contain("/v2/api/ticket/100/lock/0xabc/key/2/qr"));
    }

    [Test]
    public async Task TryFetchTicketQrCodeWithRetryAsync_TriggersGenerateOnceOn404()
    {
        var prevTimeout = Environment.GetEnvironmentVariable("UNLOCK_LOCKSMITH_QR_FETCH_TIMEOUT_MS");
        var prevInterval = Environment.GetEnvironmentVariable("UNLOCK_LOCKSMITH_QR_FETCH_INTERVAL_MS");
        try
        {
            Environment.SetEnvironmentVariable("UNLOCK_LOCKSMITH_QR_FETCH_TIMEOUT_MS", "2000");
            Environment.SetEnvironmentVariable("UNLOCK_LOCKSMITH_QR_FETCH_INTERVAL_MS", "1");

            int qrCalls = 0;
            var handler = new RecordingHandler(req =>
            {
                Assert.That(req.RequestUri, Is.Not.Null);
                var path = req.RequestUri!.AbsolutePath;

                if (path.EndsWith("/qr", StringComparison.Ordinal))
                {
                    qrCalls++;
                    return qrCalls == 1
                        ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("not found") }
                        : new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new ByteArrayContent(new byte[] { 9, 9, 9 })
                        };
                }

                if (path.EndsWith("/generate", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("ok")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("unexpected path: " + path)
                };
            });

            var client = CreateClient(handler);
            var mapping = new UnlockMappingEntry
            {
                ChainId = 100,
                LockAddress = "0xabc",
                LocksmithBase = "https://locksmith.example/"
            };

            var method = typeof(UnlockClient).GetMethod(
                "TryFetchTicketQrCodeWithRetryAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            var task = (Task)method!.Invoke(client, new object[] { mapping, new BigInteger(2), CancellationToken.None })!;
            await task.ConfigureAwait(false);

            var resultProp = task.GetType().GetProperty("Result");
            Assert.That(resultProp, Is.Not.Null);
            var result = resultProp!.GetValue(task);
            Assert.That(result, Is.Not.Null);

            var resultType = result!.GetType();
            var qr = (string?)(
                resultType.GetProperty("QrCodeDataUrl")?.GetValue(result)
                ?? resultType.GetField("Item1")?.GetValue(result));
            var warning = (string?)(
                resultType.GetProperty("Warning")?.GetValue(result)
                ?? resultType.GetField("Item2")?.GetValue(result));

            Assert.That(qr, Is.Not.Null);
            Assert.That(qr, Does.StartWith("data:image/png;base64,"));
            Assert.That(warning, Is.Null);

            var paths = handler.Requests.Select(r => r.RequestUri!.AbsolutePath).ToList();
            Assert.That(paths, Does.Contain("/v2/api/ticket/100/lock/0xabc/key/2/qr"));
            Assert.That(paths, Does.Contain("/v2/api/ticket/100/lock/0xabc/key/2/generate"));
            Assert.That(paths.Count(p => p.EndsWith("/generate", StringComparison.Ordinal)), Is.EqualTo(1));
            Assert.That(paths.Count(p => p.EndsWith("/qr", StringComparison.Ordinal)), Is.EqualTo(2));
        }
        finally
        {
            Environment.SetEnvironmentVariable("UNLOCK_LOCKSMITH_QR_FETCH_TIMEOUT_MS", prevTimeout);
            Environment.SetEnvironmentVariable("UNLOCK_LOCKSMITH_QR_FETCH_INTERVAL_MS", prevInterval);
        }
    }

    private static UnlockClient CreateClient(RecordingHandler handler)
    {
        var factory = new SimpleHttpClientFactory(handler);
        return new UnlockClient(factory, new StubLocksmithAuthProvider(), NullLogger<UnlockClient>.Instance);
    }

    private sealed class StubLocksmithAuthProvider : ILocksmithAuthProvider
    {
        public Task<string> GetAccessTokenAsync(UnlockMappingEntry mapping, CancellationToken ct)
            => Task.FromResult("stub-token");

        public Task InvalidateAsync(UnlockMappingEntry mapping, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class SimpleHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public SimpleHttpClientFactory(HttpMessageHandler handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name)
        {
            // `UnlockClient` disposes the returned `HttpClient` per request; keep the handler alive.
            return new HttpClient(_handler, disposeHandler: false);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public List<HttpRequestMessage> Requests { get; } = new();

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_responder(request));
        }
    }
}
