using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Circles.Market.Shared.Auth;

/// <summary>
/// Startup-time validator that fetches the auth-service's canonical audience catalog
/// and asserts every audience the local app expects is present in that catalog.
/// Fails fast on drift — preventing a deployed service from silently running with an
/// audience name the auth-service has never heard of.
///
/// Wire it from <see cref="AuthServiceJwksExtensions"/> at registration time so a
/// misconfigured deploy never makes it past container start.
/// </summary>
public static class AudienceCatalogValidator
{
    /// <summary>
    /// Fetch the canonical audience catalog and assert <paramref name="expectedAudiences"/>
    /// is a subset. Throws <see cref="InvalidOperationException"/> on drift or on
    /// repeated network failure. Set <c>SKIP_AUDIENCE_CATALOG_CHECK=1</c> to bypass
    /// entirely (intended for CI / local dev where auth-service is not running).
    /// Retries transient network failures up to 3 times with 2-second backoff.
    /// </summary>
    public static async Task EnsureKnownAsync(
        string authServiceUrl,
        IEnumerable<string> expectedAudiences,
        ILogger log,
        CancellationToken ct = default)
    {
        if (Environment.GetEnvironmentVariable("SKIP_AUDIENCE_CATALOG_CHECK") == "1")
        {
            string aspEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "";
            if (string.Equals(aspEnv, "Production", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "SKIP_AUDIENCE_CATALOG_CHECK=1 is forbidden in Production. " +
                    "Drift checks must run in production. Remove the env var or change ASPNETCORE_ENVIRONMENT.");
            }
            log.LogWarning(
                "SKIP_AUDIENCE_CATALOG_CHECK=1 is set (ASPNETCORE_ENVIRONMENT={Env}) — drift check disabled. " +
                "Acceptable for CI/local dev only.", aspEnv);
            return;
        }

        var expected = expectedAudiences.Distinct().ToArray();
        if (expected.Length == 0) return;

        string url = $"{authServiceUrl.TrimEnd('/')}/audiences";

        AudiencesResponse? body = null;
        Exception? lastError = null;
        // 5 attempts with exponential backoff (2s, 4s, 8s, 16s, 32s ≈ 62s total) gives
        // enough headroom to ride through a full rolling-deploy cycle of one auth-service node.
        const int maxAttempts = 5;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                body = await http.GetFromJsonAsync<AudiencesResponse>(url, ct);
                lastError = null;
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                lastError = ex;
                if (attempt < maxAttempts)
                {
                    var backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    log.LogWarning(
                        "Audience catalog fetch attempt {Attempt}/{Max} from {Url} failed: {Error}. Retrying in {Backoff}s.",
                        attempt, maxAttempts, url, ex.Message, backoff.TotalSeconds);
                    await Task.Delay(backoff, ct);
                }
            }
        }

        if (lastError is not null)
        {
            throw new InvalidOperationException(
                $"Audience catalog fetch from {url} failed after {maxAttempts} attempts. " +
                "Marketplace cannot validate its expected audiences against the auth-service. " +
                "Set SKIP_AUDIENCE_CATALOG_CHECK=1 if running in CI / without auth-service. " +
                $"Last cause: {lastError.Message}", lastError);
        }

        if (body is null || body.Audiences is null || body.Audiences.Length == 0)
            throw new InvalidOperationException(
                $"Audience catalog at {url} returned empty body. " +
                "Auth-service may be misconfigured or the endpoint contract has changed.");

        var known = new HashSet<string>(body.Audiences.Select(a => a.Name), StringComparer.Ordinal);
        var missing = expected.Where(a => !known.Contains(a)).ToArray();

        if (missing.Length > 0)
        {
            string knownList = string.Join(", ", known.OrderBy(x => x));
            throw new InvalidOperationException(
                $"Audience catalog drift detected at startup. " +
                $"This app expects audiences [{string.Join(", ", missing)}] but the auth-service " +
                $"at {url} only knows [{knownList}]. " +
                "Update the auth-service AUDIENCES constant or correct the local audience config.");
        }

        log.LogInformation(
            "Audience catalog validated against {Url}: expected=[{Expected}], all known.",
            url, string.Join(", ", expected));
    }

    private sealed record AudiencesResponse(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("audiences")] AudienceItem[] Audiences);

    private sealed record AudienceItem(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("ttlMinutes")] int TtlMinutes,
        [property: JsonPropertyName("defaultTtlMinutes")] int DefaultTtlMinutes,
        [property: JsonPropertyName("description")] string Description);
}
