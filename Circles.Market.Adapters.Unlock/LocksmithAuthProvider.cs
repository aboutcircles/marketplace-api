using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Collections.Concurrent;
using Nethereum.Signer;
using Nethereum.Siwe.Core;

namespace Circles.Market.Adapters.Unlock;

public interface ILocksmithAuthProvider
{
    Task<string> GetAccessTokenAsync(UnlockMappingEntry mapping, CancellationToken ct);
    Task InvalidateAsync(UnlockMappingEntry mapping, CancellationToken ct);
}

public sealed class LocksmithAuthProvider : ILocksmithAuthProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LocksmithAuthProvider> _log;
    private readonly object _sync = new();
    private readonly Dictionary<string, TokenCacheEntry> _tokenCache = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _singleFlight = new();

    public LocksmithAuthProvider(IHttpClientFactory httpClientFactory, ILogger<LocksmithAuthProvider> log)
    {
        _httpClientFactory = httpClientFactory;
        _log = log;
    }

    public async Task<string> GetAccessTokenAsync(UnlockMappingEntry mapping, CancellationToken ct)
    {
        var cacheKey = BuildCacheKey(mapping);
        var gate = _singleFlight.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var skew = TimeSpan.FromSeconds(GetEnvInt("LOCKSMITH_ACCESS_TOKEN_REFRESH_SKEW_SECONDS", 30));

            lock (_sync)
            {
                if (_tokenCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > now.Add(skew))
                {
                    _log.LogDebug("Locksmith token cache hit. chain={Chain} seller={Seller}", mapping.ChainId, mapping.Seller);
                    return cached.Token;
                }
            }

            _log.LogInformation("Locksmith token cache miss. chain={Chain} seller={Seller}", mapping.ChainId, mapping.Seller);

            var token = await LoginAsync(mapping, ct);
            var ttl = TimeSpan.FromSeconds(GetEnvInt("LOCKSMITH_ACCESS_TOKEN_TTL_SECONDS", 900));
            var expiresAt = DateTimeOffset.UtcNow.Add(ttl);

            lock (_sync)
            {
                _tokenCache[cacheKey] = new TokenCacheEntry(token, expiresAt);
            }

            return token;
        }
        finally
        {
            gate.Release();
        }
    }

    public Task InvalidateAsync(UnlockMappingEntry mapping, CancellationToken ct)
    {
        var cacheKey = BuildCacheKey(mapping);
        lock (_sync)
        {
            _tokenCache.Remove(cacheKey);
        }

        _singleFlight.TryRemove(cacheKey, out _);

        _log.LogInformation("Locksmith token cache invalidated. chain={Chain} seller={Seller}", mapping.ChainId, mapping.Seller);
        return Task.CompletedTask;
    }

    private async Task<string> LoginAsync(UnlockMappingEntry mapping, CancellationToken ct)
    {
        using var client = _httpClientFactory.CreateClient();
        var baseUri = new Uri(mapping.LocksmithBase.Trim().TrimEnd('/') + "/");
        client.BaseAddress = baseUri;

        _log.LogInformation("Locksmith nonce fetch start. chain={Chain} seller={Seller}", mapping.ChainId, mapping.Seller);
        var nonce = await GetNonceAsync(client, ct);
        _log.LogInformation("Locksmith nonce fetch success. chain={Chain} seller={Seller}", mapping.ChainId, mapping.Seller);

        var chainId = GetEnvInt("LOCKSMITH_SIWE_CHAIN_ID", (int)mapping.ChainId);
        var domain = Environment.GetEnvironmentVariable("LOCKSMITH_SIWE_DOMAIN");
        if (string.IsNullOrWhiteSpace(domain))
            domain = baseUri.Host;

        var uri = Environment.GetEnvironmentVariable("LOCKSMITH_SIWE_URI");
        if (string.IsNullOrWhiteSpace(uri))
            uri = baseUri.GetLeftPart(UriPartial.Authority);

        var account = new EthECKey(mapping.ServicePrivateKey.Trim());
        var address = account.GetPublicAddress();

        var siweMessage = new SiweMessage
        {
            Domain = domain,
            Address = address,
            Statement = "Sign in with Ethereum to Unlock Protocol.",
            Uri = uri,
            Version = "1",
            ChainId = chainId.ToString(),
            Nonce = nonce
        };
        siweMessage.SetIssuedAtNow();

        var message = SiweMessageStringBuilder.BuildMessage(siweMessage);
        var signature = new EthereumMessageSigner().EncodeUTF8AndSign(message, account);

        _log.LogInformation("Locksmith SIWE login start. chain={Chain} seller={Seller}", mapping.ChainId, mapping.Seller);

        using var response = await client.PostAsJsonAsync("v2/auth/login", new LocksmithLoginRequest
        {
            Message = message,
            Signature = signature
        }, ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            _log.LogError("Locksmith SIWE login failure. chain={Chain} seller={Seller} status={StatusCode}", mapping.ChainId, mapping.Seller, (int)response.StatusCode);
            throw new HttpRequestException(
                $"Locksmith login returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}",
                inner: null,
                statusCode: response.StatusCode);
        }

        var login = JsonSerializer.Deserialize<LocksmithLoginResponse>(body, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (login is null || string.IsNullOrWhiteSpace(login.AccessToken))
        {
            _log.LogError("Locksmith SIWE login failure. chain={Chain} seller={Seller} reason=missing-token", mapping.ChainId, mapping.Seller);
            throw new InvalidOperationException("Locksmith login succeeded but did not return an access token.");
        }

        _log.LogInformation("Locksmith SIWE login success. chain={Chain} seller={Seller}", mapping.ChainId, mapping.Seller);
        return login.AccessToken.Trim();
    }

    private async Task<string> GetNonceAsync(HttpClient client, CancellationToken ct)
    {
        using var response = await client.GetAsync("v2/auth/nonce", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Locksmith nonce returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}",
                inner: null,
                statusCode: response.StatusCode);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            throw new InvalidOperationException("Locksmith nonce response was empty.");
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.String)
            {
                var value = doc.RootElement.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }

            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("nonce", out var nonceProp) &&
                nonceProp.ValueKind == JsonValueKind.String)
            {
                var value = nonceProp.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "Locksmith nonce JSON parse failed; falling back to raw nonce body parsing");
            Activity.Current?.AddEvent(new ActivityEvent("unlock.locksmith.nonce_json_parse_failed"));
            Activity.Current?.SetTag("unlock.locksmith.nonce_json_error", ex.Message);
        }

        return body.Trim().Trim('"');
    }

    private static string BuildCacheKey(UnlockMappingEntry mapping)
    {
        var signer = new EthECKey(mapping.ServicePrivateKey.Trim()).GetPublicAddress().Trim().ToLowerInvariant();
        var baseUri = new Uri(mapping.LocksmithBase.Trim().TrimEnd('/') + "/");
        var domain = Environment.GetEnvironmentVariable("LOCKSMITH_SIWE_DOMAIN");
        if (string.IsNullOrWhiteSpace(domain))
            domain = baseUri.Host;

        var uri = Environment.GetEnvironmentVariable("LOCKSMITH_SIWE_URI");
        if (string.IsNullOrWhiteSpace(uri))
            uri = baseUri.GetLeftPart(UriPartial.Authority);

        var chainId = GetEnvInt("LOCKSMITH_SIWE_CHAIN_ID", (int)mapping.ChainId);
        return $"{chainId}:{baseUri.Host.ToLowerInvariant()}:{signer}:{domain.Trim().ToLowerInvariant()}:{uri.Trim().ToLowerInvariant()}";
    }

    private static int GetEnvInt(string name, int defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : defaultValue;
    }

    private sealed record TokenCacheEntry(string Token, DateTimeOffset ExpiresAt);

    private sealed class LocksmithLoginRequest
    {
        public string Message { get; init; } = string.Empty;
        public string Signature { get; init; } = string.Empty;
    }

    private sealed class LocksmithLoginResponse
    {
        public string AccessToken { get; init; } = string.Empty;
    }
}
