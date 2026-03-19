using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Circles.Market.Shared.Auth;

/// <summary>
/// Fallback authentication via the auth-service token exchange endpoint.
/// When direct JWT validation fails (e.g. the token was issued for a different audience),
/// this service attempts to exchange the token for a Circles JWT and returns a
/// <see cref="ClaimsPrincipal"/> with the same claims the normal JWT path would produce.
///
/// Results are cached by token hash so repeated requests with the same token skip the
/// HTTP round-trip.
/// </summary>
public sealed class TokenExchangeService
{
    private const string HttpClientName = "token-exchange";
    private const string CachePrefix = "tkn_ex:";

    private readonly IHttpClientFactory _httpFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TokenExchangeService> _log;
    private readonly string _exchangeUrl;
    private readonly string _audience;

    public TokenExchangeService(
        IHttpClientFactory httpFactory,
        IMemoryCache cache,
        ILogger<TokenExchangeService> log)
    {
        _httpFactory = httpFactory;
        _cache = cache;
        _log = log;

        string authServiceUrl = Environment.GetEnvironmentVariable("AUTH_SERVICE_URL")
            ?? throw new InvalidOperationException("AUTH_SERVICE_URL is required");
        _audience = Environment.GetEnvironmentVariable("AUTH_JWT_AUDIENCE") ?? "market-api";
        _exchangeUrl = $"{authServiceUrl.TrimEnd('/')}/exchange";
    }

    /// <summary>
    /// Attempt to exchange <paramref name="rawToken"/> for a Circles JWT via the auth service.
    /// Returns a <see cref="ClaimsPrincipal"/> on success, or <c>null</c> on any failure.
    /// Never throws.
    /// </summary>
    public async Task<ClaimsPrincipal?> TryExchangeAsync(string rawToken, CancellationToken ct = default)
    {
        try
        {
            string cacheKey = CachePrefix + HashToken(rawToken);

            if (_cache.TryGetValue(cacheKey, out ClaimsPrincipal? cached))
            {
                _log.LogDebug("Token exchange cache hit");
                return cached;
            }

            _log.LogDebug("Attempting token exchange at {Url}", _exchangeUrl);

            using var client = _httpFactory.CreateClient(HttpClientName);
            var payload = new ExchangeRequest(rawToken, _audience);

            using var response = await client.PostAsJsonAsync(_exchangeUrl, payload, ct);

            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning(
                    "Token exchange returned {StatusCode} from {Url}",
                    (int)response.StatusCode, _exchangeUrl);
                return null;
            }

            var body = await response.Content.ReadFromJsonAsync<ExchangeResponse>(ct);
            if (body is null || string.IsNullOrEmpty(body.Address))
            {
                _log.LogWarning("Token exchange returned empty or unparseable body");
                return null;
            }

            string address = body.Address.ToLowerInvariant();
            string chainId = body.ChainId.ToString();

            var claims = new List<Claim>
            {
                new("addr", address),
                new("chainId", chainId),
                new("sub", $"{address}@{chainId}"),
                new(ClaimTypes.NameIdentifier, address),
            };

            var identity = new ClaimsIdentity(claims, "TokenExchange");
            var principal = new ClaimsPrincipal(identity);

            var expiration = body.ExpiresIn > 0
                ? DateTimeOffset.UtcNow.AddSeconds(body.ExpiresIn)
                : DateTimeOffset.UtcNow.AddMinutes(5);

            var cacheOpts = new MemoryCacheEntryOptions
            {
                AbsoluteExpiration = expiration,
                Size = 1, // Required for size-limited caches (e.g. Api's 200MB limit)
            };
            _cache.Set(cacheKey, principal, cacheOpts);

            _log.LogDebug(
                "Token exchange succeeded for {Address} (chain {ChainId}, exchanged from {ExchangedFrom})",
                address, chainId, body.ExchangedFrom);

            return principal;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _log.LogDebug("Token exchange cancelled");
            return null;
        }
        catch (OperationCanceledException)
        {
            // HttpClient timeout surfaces as TaskCanceledException (subclass of
            // OperationCanceledException) with a non-cancelled token.
            _log.LogWarning("Token exchange timed out for {Url}", _exchangeUrl);
            return null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Token exchange failed unexpectedly");
            return null;
        }
    }

    private static string HashToken(string token)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexStringLower(hash);
    }

    // ---- DTOs ----

    private sealed record ExchangeRequest(
        [property: JsonPropertyName("token")] string Token,
        [property: JsonPropertyName("audience")] string Audience);

    private sealed record ExchangeResponse(
        [property: JsonPropertyName("token")] string Token,
        [property: JsonPropertyName("address")] string Address,
        [property: JsonPropertyName("chainId")] int ChainId,
        [property: JsonPropertyName("expiresIn")] int ExpiresIn,
        [property: JsonPropertyName("exchangedFrom")] string ExchangedFrom);
}
