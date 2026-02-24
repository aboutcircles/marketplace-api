using Circles.Market.Api.Catalog;
using Circles.Market.Api.Routing;
using Circles.Market.Shared.Admin;
using Circles.Profiles.Market;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace Circles.Market.Api.Service;

public static class ServiceEndpoints
{
    public static void MapServiceApi(this IEndpointRouteBuilder app, string pgConnectionString)
    {
        // Readiness endpoint: checks critical dependencies before reporting healthy
        app.MapGet("/health", async (CancellationToken ct) =>
        {
            var checks = new Dictionary<string, string>();
            var allOk = true;

            // PostgreSQL connectivity check
            try
            {
                await using var conn = new NpgsqlConnection(pgConnectionString);
                await conn.OpenAsync(ct);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT 1";
                await cmd.ExecuteScalarAsync(ct);
                checks["postgres"] = "ok";
            }
            catch (Exception)
            {
                checks["postgres"] = "failed";
                allOk = false;
            }

            var statusCode = allOk ? 200 : 503;
            return Results.Json(new { ok = allOk, checks }, statusCode: statusCode);
        });

        app.MapGet("/api/operator/{op}/catalog",
                (string op,
                        long? chainId,
                        long? start,
                        long? end,
                        int? pageSize,
                        string? cursor,
                        int? offset,
                        HttpContext ctx,
                        IMarketRouteStore routes,
                        OperatorCatalogService opCatalog,
                        CancellationToken ct,
                        [FromServices] ILogger<OperatorCatalogService> logger)
                    => OperatorCatalogEndpoint.Handle(op, chainId, start, end, pageSize, cursor, offset, ctx, routes, opCatalog, ct,
                        logger))
            .WithName("OperatorAggregatedCatalog")
            .WithSummary(
                "Aggregates verified product/* links across many avatars under the operator namespace and returns a paged AggregatedCatalog.")
            .WithDescription(
                "Inputs: operator address path param; repeated ?avatars=...; optional chainId/start/end; cursor/offset pagination. Implements CPA rules (verification, chain domain, nonce replay, time window) and reduces to newest-first product catalog with tombstone support.");

        app.MapGet(MarketConstants.Routes.Sellers,
                async (IMarketRouteStore routes, CancellationToken ct) =>
                {
                    var sellers = await routes.GetActiveSellersAsync(ct);
                    return Results.Json(new
                    {
                        sellers = sellers.Select(seller => new
                        {
                            chainId = seller.ChainId,
                            seller = seller.SellerAddress
                        })
                    });
                })
            .WithName("ActiveSellers")
            .WithSummary("List all active seller addresses with at least one enabled route.")
            .WithDescription("Returns distinct seller addresses from enabled market routes. Sellers are returned as lowercase eip155 addresses with their chainId.");
    }
}
