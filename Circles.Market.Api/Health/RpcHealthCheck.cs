using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Circles.Market.Api.Health;

/// <summary>
/// Checks circles RPC host readiness (nethermind + indexer + pathfinder + DB).
/// Uses CIRCLES_RPC env var (Docker-internal: http://rpc:8080).
/// </summary>
public class RpcHealthCheck(IHttpClientFactory httpClientFactory, string rpcBaseUrl) : IHealthCheck
{
    private readonly string _readyUrl = rpcBaseUrl.TrimEnd('/') + "/ready";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            using var response = await client.GetAsync(_readyUrl, ct);

            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy("rpc ready")
                : HealthCheckResult.Unhealthy($"rpc returned {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("rpc unreachable", ex);
        }
    }
}
