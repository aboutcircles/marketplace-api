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
            Assert.That(req.RequestUri!.AbsolutePath, Is.EqualTo("/v2/api/ticket/100/lock/0xabc/key/2/qr"));

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
        Assert.That(handler.Requests, Has.Count.EqualTo(1));
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

    [Test]
    public async Task TrySendTicketEmailAsync_UsesExpectedPathAndReturnsTrueOnSuccess()
    {
        var handler = new RecordingHandler(req =>
        {
            Assert.That(req.Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(req.RequestUri, Is.Not.Null);
            Assert.That(req.RequestUri!.AbsolutePath, Is.EqualTo("/v2/api/ticket/100/0xabc/2/email"));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"sent\":true}")
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
            "TrySendTicketEmailAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null);

        var task = (Task)method!.Invoke(client, new object[] { mapping, new BigInteger(2), CancellationToken.None })!;
        await task.ConfigureAwait(false);
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var sent = (bool)result.GetType().GetProperty("Success")!.GetValue(result)!;

        Assert.That(sent, Is.True);
        Assert.That(handler.Requests, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task TrySendTicketEmailAsync_ReturnsFalseOnNonSuccessStatus()
    {
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("forbidden")
            });

        var client = CreateClient(handler);
        var mapping = new UnlockMappingEntry
        {
            ChainId = 100,
            LockAddress = "0xabc",
            LocksmithBase = "https://locksmith.example/"
        };

        var method = typeof(UnlockClient).GetMethod(
            "TrySendTicketEmailAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null);

        var task = (Task)method!.Invoke(client, new object[] { mapping, new BigInteger(2), CancellationToken.None })!;
        await task.ConfigureAwait(false);
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var sent = (bool)result.GetType().GetProperty("Success")!.GetValue(result)!;

        Assert.That(sent, Is.False);
        Assert.That(handler.Requests, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task TryUpdateKeyMetadataAsync_SendsEmailAndNameMetadata()
    {
        string? requestBody = null;
        var handler = new RecordingHandler(req =>
        {
            Assert.That(req.Method, Is.EqualTo(HttpMethod.Put));
            Assert.That(req.RequestUri, Is.Not.Null);
            Assert.That(req.RequestUri!.AbsolutePath, Is.EqualTo("/v2/api/metadata/100/locks/0xabc/keys/2"));
            requestBody = req.Content is null ? null : req.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
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
            "TryUpdateKeyMetadataAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null);

        var recipient = new UnlockRecipientInfo
        {
            Email = "alice@example.com",
            GivenName = "Alice",
            FamilyName = "Doe"
        };

        var task = (Task)method!.Invoke(client, new object[] { mapping, new BigInteger(2), "0xbuyer", recipient, CancellationToken.None })!;
        await task.ConfigureAwait(false);
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var ok = (bool)result.GetType().GetProperty("Success")!.GetValue(result)!;

        Assert.That(ok, Is.True);
        Assert.That(handler.Requests, Has.Count.EqualTo(1));
        Assert.That(requestBody, Is.Not.Null);
        Assert.That(requestBody, Does.Contain("\"email\":\"alice@example.com\""));
        Assert.That(requestBody, Does.Contain("\"givenName\":\"Alice\""));
        Assert.That(requestBody, Does.Contain("\"familyName\":\"Doe\""));
        Assert.That(requestBody, Does.Contain("\"name\":\"Alice Doe\""));
    }

    [Test]
    public async Task TryUpdateKeyMetadataAsync_ReturnsFalseOnNonSuccessStatus()
    {
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("forbidden")
            });

        var client = CreateClient(handler);
        var mapping = new UnlockMappingEntry
        {
            ChainId = 100,
            LockAddress = "0xabc",
            LocksmithBase = "https://locksmith.example/"
        };

        var method = typeof(UnlockClient).GetMethod(
            "TryUpdateKeyMetadataAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null);

        var recipient = new UnlockRecipientInfo
        {
            Email = "alice@example.com"
        };

        var task = (Task)method!.Invoke(client, new object[] { mapping, new BigInteger(2), "0xbuyer", recipient, CancellationToken.None })!;
        await task.ConfigureAwait(false);
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var ok = (bool)result.GetType().GetProperty("Success")!.GetValue(result)!;

        Assert.That(ok, Is.False);
    }

    private static UnlockClient CreateClient(RecordingHandler handler)
    {
        var factory = new SimpleHttpClientFactory(handler);
        return new UnlockClient(factory, NullLogger<UnlockClient>.Instance);
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
