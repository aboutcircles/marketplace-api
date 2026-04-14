using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Circles.Market.Api.Health;

public class MarketDatabaseHealthCheck(string connectionString) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync(ct);
            return HealthCheckResult.Healthy("market-db ok");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("market-db unreachable", ex);
        }
    }
}
