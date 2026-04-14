using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Circles.Market.Api.Health;

/// <summary>
/// Checks auth-service reachability via its JWKS endpoint.
/// Uses AUTH_SERVICE_URL env var. JWKS is cached by the JWT middleware,
/// so a transient outage is Degraded (not Unhealthy) — existing tokens still validate.
/// </summary>
public class AuthServiceHealthCheck(IHttpClientFactory httpClientFactory, string authServiceUrl) : IHealthCheck
{
    private readonly string _jwksUrl = authServiceUrl.TrimEnd('/') + "/.well-known/jwks.json";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            var response = await client.GetAsync(_jwksUrl, ct);

            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy("auth-service ok")
                : HealthCheckResult.Degraded($"auth-service returned {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Degraded("auth-service unreachable", ex);
        }
    }
}
