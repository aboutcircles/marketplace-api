using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Circles.Market.Shared.Auth;

/// <summary>
/// Fetches and caches JWKS (JSON Web Key Set) signing keys from an external auth service.
///
/// Reads (<see cref="ResolveSigningKeys"/>) are served from an in-memory snapshot only and
/// never perform I/O; <see cref="JwksRefreshService"/> keeps the snapshot warm in the
/// background. Keys older than <see cref="CacheDuration"/> are served stale with a warning
/// for up to <see cref="MaxStaleness"/> past expiry, after which all tokens are rejected.
/// The staleness window is anchored at cache expiry (FetchedAt + CacheDuration) rather
/// than at the first failed fetch — strictly tighter than the previous fetch-on-read
/// policy, bounding key age at CacheDuration + MaxStaleness (70 min with defaults).
/// </summary>
public sealed class JwksKeyManager : IDisposable
{
    private static readonly TimeSpan DefaultCacheDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DefaultMaxStaleness = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan LogThrottleInterval = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly string _jwksUrl;
    private readonly ILogger<JwksKeyManager> _log;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _lock = new(1, 1);

    internal TimeSpan CacheDuration { get; }
    internal TimeSpan MaxStaleness { get; }

    private sealed record Snapshot(IReadOnlyList<SecurityKey> Keys, DateTimeOffset FetchedAt);

    private volatile Snapshot? _snapshot;
    private long _lastStaleWarnTicks;
    private long _lastStaleErrorTicks;

    public JwksKeyManager(
        string jwksUrl, ILogger<JwksKeyManager> log,
        TimeSpan? cacheDuration = null, TimeSpan? maxStaleness = null)
        : this(jwksUrl, log, new HttpClient { Timeout = TimeSpan.FromSeconds(10) },
            TimeProvider.System, cacheDuration, maxStaleness)
    {
    }

    internal JwksKeyManager(
        string jwksUrl, ILogger<JwksKeyManager> log, HttpClient http, TimeProvider time,
        TimeSpan? cacheDuration = null, TimeSpan? maxStaleness = null)
    {
        _jwksUrl = jwksUrl;
        _log = log;
        _http = http; // owned: disposed with the manager
        _time = time;
        CacheDuration = cacheDuration ?? DefaultCacheDuration;
        MaxStaleness = maxStaleness ?? DefaultMaxStaleness;
    }

    /// <summary>True while the snapshot is within <see cref="CacheDuration"/>.</summary>
    internal bool IsFresh
    {
        get
        {
            Snapshot? snap = _snapshot;
            return snap is not null && IsWithinCache(snap, _time.GetUtcNow());
        }
    }

    /// <summary>
    /// Unconditionally fetches the JWKS document and replaces the snapshot.
    /// Serialized internally; throws on fetch/parse failure — or when the document
    /// contains no usable signing keys — without touching the existing snapshot.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            _log.LogDebug("Fetching JWKS from {Url}", _jwksUrl);

            var response = await _http.GetAsync(_jwksUrl, ct);
            response.EnsureSuccessStatusCode();

            var jwksJson = await response.Content.ReadAsStringAsync(ct);
            var jwks = new JsonWebKeySet(jwksJson);

            var keys = jwks.GetSigningKeys().ToList();
            if (keys.Count == 0)
            {
                throw new InvalidOperationException(
                    $"JWKS at {_jwksUrl} returned no usable signing keys");
            }

            _snapshot = new Snapshot(keys, _time.GetUtcNow());

            _log.LogInformation("JWKS refreshed, found {KeyCount} signing keys", keys.Count);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Awaitable accessor kept for in-assembly/test use: returns fresh keys, fetching if
    /// the cache is expired; on fetch failure falls back to stale keys within the
    /// staleness window. The hosted refresher uses <see cref="RefreshAsync"/> directly.
    /// </summary>
    internal async Task<IReadOnlyList<SecurityKey>> GetSigningKeysAsync(CancellationToken ct = default)
    {
        Snapshot? snap = _snapshot;
        if (snap is not null && IsWithinCache(snap, _time.GetUtcNow()))
            return snap.Keys;

        try
        {
            await RefreshAsync(ct);
            return _snapshot!.Keys;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogError(ex, "Failed to fetch JWKS from {Url}", _jwksUrl);
            if (_snapshot is null) throw; // no fallback — surface the root cause
            return ServeFromSnapshotOrThrow();
        }
    }

    /// <summary>
    /// Synchronous adapter for <see cref="TokenValidationParameters.IssuerSigningKeyResolver"/>.
    /// Cache-only: never performs I/O on the request path.
    /// </summary>
    public IEnumerable<SecurityKey> ResolveSigningKeys(
        string token, SecurityToken securityToken, string kid,
        TokenValidationParameters validationParameters)
    {
        return ServeFromSnapshotOrThrow();
    }

    private bool IsWithinCache(Snapshot snap, DateTimeOffset now) =>
        now < snap.FetchedAt + CacheDuration;

    private IReadOnlyList<SecurityKey> ServeFromSnapshotOrThrow()
    {
        Snapshot? snap = _snapshot;
        if (snap is null)
        {
            throw new InvalidOperationException(
                $"JWKS keys unavailable: no successful fetch from {_jwksUrl} yet");
        }

        DateTimeOffset now = _time.GetUtcNow();
        if (IsWithinCache(snap, now))
            return snap.Keys;

        TimeSpan staleness = now - (snap.FetchedAt + CacheDuration);
        if (staleness > MaxStaleness)
        {
            // Only the log line is throttled; the throw happens on every call.
            if (ShouldLogThrottled(ref _lastStaleErrorTicks, now))
            {
                _log.LogError(
                    "JWKS keys stale for {Minutes:F0}min (exceeds {Max}min limit), rejecting all tokens",
                    staleness.TotalMinutes, MaxStaleness.TotalMinutes);
            }

            throw new InvalidOperationException(
                $"JWKS keys stale beyond the {MaxStaleness.TotalMinutes:F0}min limit");
        }

        // Throttle the stale warning so a busy host doesn't emit it per request.
        if (ShouldLogThrottled(ref _lastStaleWarnTicks, now))
        {
            _log.LogWarning(
                "Using stale JWKS keys ({Minutes:F0}min past expiry, max {Max}min)",
                staleness.TotalMinutes, MaxStaleness.TotalMinutes);
        }

        return snap.Keys;
    }

    private static bool ShouldLogThrottled(ref long lastLogTicks, DateTimeOffset now)
    {
        long nowTicks = now.UtcTicks;
        long last = Interlocked.Read(ref lastLogTicks);
        return nowTicks - last > LogThrottleInterval.Ticks &&
               Interlocked.CompareExchange(ref lastLogTicks, nowTicks, last) == last;
    }

    public void Dispose()
    {
        _http.Dispose();
        _lock.Dispose();
    }
}
