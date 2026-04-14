using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Circles.Market.Api.Health;

/// <summary>
/// Checks profile-pinning service reachability via its /health/ready endpoint.
/// Uses PINNING_SERVICE_URL env var (Docker-internal: http://profile-pinning:3000).
/// Unhealthy when down — market-api can't resolve profiles or pin new content.
/// </summary>
public class PinningServiceHealthCheck(IHttpClientFactory httpClientFactory, string pinningServiceUrl) : IHealthCheck
{
    private readonly string _readyUrl = pinningServiceUrl.TrimEnd('/') + "/health/ready";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            var response = await client.GetAsync(_readyUrl, ct);

            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy("profile-pinning ok")
                : HealthCheckResult.Unhealthy($"profile-pinning returned {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("profile-pinning unreachable", ex);
        }
    }
}
