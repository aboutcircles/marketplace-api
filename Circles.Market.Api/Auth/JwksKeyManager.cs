using Microsoft.IdentityModel.Tokens;

namespace Circles.Market.Api.Auth;

/// <summary>
/// Fetches and caches JWKS (JSON Web Key Set) signing keys from an external auth service.
/// Thread-safe with 10-minute cache and graceful fallback to stale keys on fetch failure.
/// </summary>
public sealed class JwksKeyManager : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _jwksUrl;
    private readonly ILogger<JwksKeyManager> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(10);

    private IList<SecurityKey>? _cachedKeys;
    private DateTimeOffset _cacheExpiry = DateTimeOffset.MinValue;

    public JwksKeyManager(string jwksUrl, ILogger<JwksKeyManager> log)
    {
        _jwksUrl = jwksUrl;
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>
    /// Gets the signing keys from the JWKS endpoint, using cached values if available.
    /// </summary>
    public async Task<IList<SecurityKey>> GetSigningKeysAsync(CancellationToken ct = default)
    {
        // Fast path: return cached keys if still valid
        if (_cachedKeys is not null && DateTimeOffset.UtcNow < _cacheExpiry)
        {
            return _cachedKeys;
        }

        await _lock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (_cachedKeys is not null && DateTimeOffset.UtcNow < _cacheExpiry)
            {
                return _cachedKeys;
            }

            _log.LogDebug("Fetching JWKS from {Url}", _jwksUrl);

            var response = await _http.GetAsync(_jwksUrl, ct);
            response.EnsureSuccessStatusCode();

            var jwksJson = await response.Content.ReadAsStringAsync(ct);
            var jwks = new JsonWebKeySet(jwksJson);

            _cachedKeys = jwks.GetSigningKeys().ToList();
            _cacheExpiry = DateTimeOffset.UtcNow.Add(_cacheDuration);

            _log.LogInformation("JWKS refreshed, found {KeyCount} signing keys", _cachedKeys.Count);

            return _cachedKeys;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to fetch JWKS from {Url}", _jwksUrl);

            // If we have stale keys, use them rather than failing completely
            if (_cachedKeys is not null)
            {
                _log.LogWarning("Using stale JWKS keys due to refresh failure");
                return _cachedKeys;
            }

            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Synchronous adapter for <see cref="TokenValidationParameters.IssuerSigningKeyResolver"/>.
    /// Safe to block here: most calls hit cache, and the HTTP client has a short timeout.
    /// </summary>
    public IEnumerable<SecurityKey> ResolveSigningKeys(
        string token,
        SecurityToken securityToken,
        string kid,
        TokenValidationParameters validationParameters)
    {
        return GetSigningKeysAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _http.Dispose();
        _lock.Dispose();
    }
}
