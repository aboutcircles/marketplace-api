using Circles.Market.Api.Catalog;
using Circles.Market.Api.Inventory;
using Circles.Market.Api.Routing;
using Circles.Market.Shared.Admin;
using Circles.Profiles.Market;
using Microsoft.AspNetCore.Mvc;

namespace Circles.Market.Api.Service;

public static class ServiceEndpoints
{
    public static void MapServiceApi(this IEndpointRouteBuilder app)
    {
        // Health endpoint for container orchestration
        app.MapGet("/health", () => Results.Json(new { ok = true }));

        app.MapGet("/api/operator/{op}/catalog",
                (string op,
                        long? chainId,
                        long? start,
                        long? end,
                        int? pageSize,
                        string? cursor,
                        int? offset,
                        bool? includeTotalAvailability,
                        HttpContext ctx,
                        IMarketRouteStore routes,
                        OperatorCatalogService opCatalog,
                        ILiveInventoryClient inventoryClient,
                        CancellationToken ct,
                        [FromServices] ILogger<OperatorCatalogService> logger)
                    => OperatorCatalogEndpoint.Handle(op, chainId, start, end, pageSize, cursor, offset, includeTotalAvailability, ctx, routes, opCatalog, inventoryClient, ct,
                        logger))
            .WithName("OperatorAggregatedCatalog")
            .WithSummary(
                "Aggregates verified product/* links across many avatars under the operator namespace and returns a paged AggregatedCatalog.")
            .WithDescription(
                "Inputs: operator address path param; repeated ?avatars=...; optional chainId/start/end; cursor/offset pagination; optional includeTotalAvailability=true to fetch inventory counts. Implements CPA rules (verification, chain domain, nonce replay, time window) and reduces to newest-first product catalog with tombstone support.");

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
