using System.Net;
using System.Security.Cryptography;
using Circles.Market.Shared.Auth;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;

namespace Circles.Market.Tests;

/// <summary>
/// Cache-only read semantics of <see cref="JwksKeyManager"/>: the request-path resolver
/// must never perform I/O, stale keys are served only within the staleness window, and
/// the warm-up path in <see cref="JwksRefreshService"/> must not fail host startup.
/// Time is controlled via a manual <see cref="TimeProvider"/>; HTTP via a scripted handler.
/// </summary>
[TestFixture]
public class JwksKeyManagerTests
{
    private const string JwksUrl = "https://auth.test/.well-known/jwks.json";

    private static string MakeJwksJson()
    {
        using var rsa = RSA.Create(2048);
        RSAParameters p = rsa.ExportParameters(false);
        string n = Base64UrlEncoder.Encode(p.Modulus);
        string e = Base64UrlEncoder.Encode(p.Exponent);
        return $@"{{""keys"":[{{""kty"":""RSA"",""use"":""sig"",""kid"":""test-kid"",""alg"":""RS256"",""n"":""{n}"",""e"":""{e}""}}]}}";
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public int Calls;
        public Func<HttpResponseMessage> Respond = () => new HttpResponseMessage(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(Respond());
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    private ScriptedHandler _handler = null!;
    private ManualTime _time = null!;
    private JwksKeyManager _sut = null!;

    private static HttpResponseMessage OkJwks() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(MakeJwksJson())
    };

    [SetUp]
    public void SetUp()
    {
        _handler = new ScriptedHandler { Respond = OkJwks };
        _time = new ManualTime();
        _sut = new JwksKeyManager(
            JwksUrl,
            NullLogger<JwksKeyManager>.Instance,
            new HttpClient(_handler),
            _time);
    }

    [TearDown]
    public void TearDown()
    {
        _sut.Dispose();
        _handler.Dispose();
    }

    private IEnumerable<SecurityKey> Resolve() =>
        _sut.ResolveSigningKeys("token", null!, "test-kid", null!);

    [Test]
    public void Resolve_EmptyCache_ThrowsWithoutFetching()
    {
        Assert.Throws<InvalidOperationException>(() => Resolve());
        Assert.That(_handler.Calls, Is.Zero, "resolver must never perform I/O");
    }

    [Test]
    public async Task Resolve_AfterRefresh_ServesCacheWithoutRefetch()
    {
        await _sut.RefreshAsync();

        Assert.That(Resolve(), Is.Not.Empty);
        Assert.That(Resolve(), Is.Not.Empty);
        Assert.That(_handler.Calls, Is.EqualTo(1));
    }

    [Test]
    public async Task Resolve_StaleWithinWindow_ServesStaleWithoutFetching()
    {
        await _sut.RefreshAsync();
        _time.Advance(_sut.CacheDuration + TimeSpan.FromMinutes(1));

        Assert.That(Resolve(), Is.Not.Empty);
        Assert.That(_handler.Calls, Is.EqualTo(1));
    }

    [Test]
    public async Task Resolve_StaleBeyondWindow_RejectsTokens()
    {
        await _sut.RefreshAsync();
        _time.Advance(_sut.CacheDuration + _sut.MaxStaleness + TimeSpan.FromMinutes(1));

        Assert.Throws<InvalidOperationException>(() => Resolve());
    }

    [Test]
    public async Task Refresh_Failure_LeavesExistingSnapshotServable()
    {
        await _sut.RefreshAsync();
        _handler.Respond = () => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        Assert.ThrowsAsync<HttpRequestException>(() => _sut.RefreshAsync());
        Assert.That(Resolve(), Is.Not.Empty, "failed refresh must not clobber the snapshot");
    }

    [Test]
    public async Task Refresh_EmptyKeySet_ThrowsAndPreservesSnapshot()
    {
        await _sut.RefreshAsync();
        _handler.Respond = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(@"{""keys"":[]}")
        };

        Assert.ThrowsAsync<InvalidOperationException>(() => _sut.RefreshAsync());
        Assert.That(Resolve(), Is.Not.Empty, "empty JWKS must not clobber the snapshot");
    }

    [Test]
    public async Task GetSigningKeys_FreshCache_DoesNotFetch()
    {
        await _sut.RefreshAsync();

        var keys = await _sut.GetSigningKeysAsync();

        Assert.That(keys, Is.Not.Empty);
        Assert.That(_handler.Calls, Is.EqualTo(1));
    }

    [Test]
    public async Task GetSigningKeys_ExpiredCacheAndFetchFails_ServesStaleWithinWindow()
    {
        await _sut.RefreshAsync();
        _time.Advance(_sut.CacheDuration + TimeSpan.FromMinutes(1));
        _handler.Respond = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        var keys = await _sut.GetSigningKeysAsync();

        Assert.That(keys, Is.Not.Empty);
        Assert.That(_handler.Calls, Is.EqualTo(2), "expiry must trigger a refresh attempt");
    }

    [Test]
    public async Task GetSigningKeys_HttpClientTimeout_ServesStaleWithinWindow()
    {
        await _sut.RefreshAsync();
        _time.Advance(_sut.CacheDuration + TimeSpan.FromMinutes(1));
        // HttpClient's own 10s timeout surfaces as TaskCanceledException with a
        // TimeoutException inner while the caller's token is NOT cancelled — the
        // stale fallback must still engage.
        _handler.Respond = () => throw new TaskCanceledException("timeout", new TimeoutException());

        var keys = await _sut.GetSigningKeysAsync();

        Assert.That(keys, Is.Not.Empty, "client timeout must fall back to stale keys");
    }

    [Test]
    public async Task GetSigningKeys_FetchFailsBeyondStalenessWindow_Throws()
    {
        await _sut.RefreshAsync();
        _time.Advance(_sut.CacheDuration + _sut.MaxStaleness + TimeSpan.FromMinutes(1));
        _handler.Respond = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        Assert.ThrowsAsync<InvalidOperationException>(() => _sut.GetSigningKeysAsync());
    }

    [Test]
    public void GetSigningKeys_NoCacheAndFetchFails_Throws()
    {
        _handler.Respond = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        Assert.ThrowsAsync<HttpRequestException>(() => _sut.GetSigningKeysAsync());
    }

    [Test]
    public async Task RefreshService_WarmupFailure_DoesNotFailStartup()
    {
        _handler.Respond = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        using var service = new JwksRefreshService(_sut, NullLogger<JwksRefreshService>.Instance);

        Assert.DoesNotThrowAsync(() => service.StartAsync(CancellationToken.None));

        await service.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task RefreshService_Warmup_PopulatesCacheBeforeStartCompletes()
    {
        using var service = new JwksRefreshService(_sut, NullLogger<JwksRefreshService>.Instance);

        await service.StartAsync(CancellationToken.None);

        Assert.That(_sut.IsFresh, Is.True);
        Assert.That(Resolve(), Is.Not.Empty);

        await service.StopAsync(CancellationToken.None);
    }

    [Test]
    public void RefreshService_ComputeNextDelay_NotFresh_UsesRetryIntervalWithJitter()
    {
        // No refresh yet — manager has no snapshot, so the tight retry cadence applies:
        // 15s ±20% jitter → [12s, 18s).
        using var service = new JwksRefreshService(_sut, NullLogger<JwksRefreshService>.Instance);

        for (int i = 0; i < 20; i++)
        {
            TimeSpan delay = service.ComputeNextDelay();
            Assert.That(delay, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(12)));
            Assert.That(delay, Is.LessThan(TimeSpan.FromSeconds(18)));
        }
    }

    [Test]
    public async Task RefreshService_ComputeNextDelay_Fresh_UsesHalfCacheDurationWithJitter()
    {
        // Fresh snapshot → CacheDuration/2 (5min) ±20% jitter → [4min, 6min).
        await _sut.RefreshAsync();
        using var service = new JwksRefreshService(_sut, NullLogger<JwksRefreshService>.Instance);

        for (int i = 0; i < 20; i++)
        {
            TimeSpan delay = service.ComputeNextDelay();
            Assert.That(delay, Is.GreaterThanOrEqualTo(TimeSpan.FromMinutes(4)));
            Assert.That(delay, Is.LessThan(TimeSpan.FromMinutes(6)));
        }
    }
}
