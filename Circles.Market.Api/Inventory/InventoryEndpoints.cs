using System.Text.Json;
using Circles.Market.Api.Routing;
using Circles.Profiles.Models.Market;
using Circles.Market.Api.Outbound;

namespace Circles.Market.Api.Inventory;

public static class InventoryEndpoints
{
    public static IEndpointRouteBuilder MapInventoryApi(this IEndpointRouteBuilder app)
    {
        // NOTE: These endpoints are intended to be used in place of raw availabilityFeed/inventoryFeed URLs
        // in catalog outputs. They resolve the product by seller + sku via the seller profile namespaces
        // and apply strict validation to upstream responses per product-catalog.md §4.2.
        app.MapGet(MarketConstants.Routes.AvailabilityBase + "/{chainId}/{seller}/{sku}", GetAvailability)
            .WithSummary("Returns if the product is currently available.")
            .WithDescription("Queries the current availability of a product by chainId, seller and sku.")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status502BadGateway)
            .Produces(StatusCodes.Status508LoopDetected);

        app.MapGet(MarketConstants.Routes.InventoryBase + "/{chainId}/{seller}/{sku}", GetInventory)
            .WithSummary("Returns the inventory count of this product.")
            .WithDescription("Queries the current inventory of a product by chainId, seller and sku.")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status502BadGateway)
            .Produces(StatusCodes.Status508LoopDetected);

        return app;
    }

    private static async Task<IResult> GetAvailability(
        long chainId,
        string seller,
        string sku,
        IMarketRouteStore routes,
        IHttpClientFactory http,
        Auth.IOutboundServiceAuthProvider authProvider,
        IOneOffSalesStore oneOffStore,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (ctx.Request.Headers.ContainsKey(OutboundGuards.HopHeader))
        {
            return Results.Problem(title: "Loop detected", detail: "A proxy loop was detected.",
                statusCode: StatusCodes.Status508LoopDetected);
        }

        try
        {
            seller = Utils.NormalizeAddr(seller);
            string skuNorm = sku.Trim().ToLowerInvariant();

            var cfg = await routes.TryGetAsync(chainId, seller, skuNorm, ct);
            if (cfg is null || !cfg.IsConfigured)
            {
                return Results.Problem(title: "Not found", detail: "Product not configured",
                    statusCode: StatusCodes.Status404NotFound);
            }

            var availabilityUrl = await routes.TryResolveUpstreamAsync(chainId, seller, skuNorm, MarketServiceKind.Availability, ct);
            if (!string.IsNullOrWhiteSpace(availabilityUrl))
            {
                var avail = await FetchAvailabilityAsync(http, authProvider, availabilityUrl!, ct);
                if (avail.IsError)
                {
                    if (avail.Error == "Blocked private address")
                    {
                        return Results.Problem(title: "Upstream blocked", detail: avail.Error,
                            statusCode: StatusCodes.Status502BadGateway);
                    }
                    return Results.Problem(title: "Upstream error", detail: avail.Error,
                        statusCode: StatusCodes.Status502BadGateway);
                }

                if (avail.Value != "https://schema.org/InStock" && avail.Value != "https://schema.org/OutOfStock")
                {
                    return Results.Problem(title: "Upstream sent invalid response",
                        detail:
                        "The server sent an invalid response. Valid response values are : https://schema.org/InStock,https://schema.org/OutOfStock",
                        statusCode: StatusCodes.Status502BadGateway);
                }

                return Results.Json(avail.Value);
            }

            var inventoryUrl = await routes.TryResolveUpstreamAsync(chainId, seller, skuNorm, MarketServiceKind.Inventory, ct);
            if (!string.IsNullOrWhiteSpace(inventoryUrl))
            {
                var inv = await FetchInventoryAsync(http, authProvider, inventoryUrl!, ct);
                if (inv.IsError)
                {
                    if (inv.Error == "Blocked private address")
                    {
                        return Results.Problem(title: "Upstream blocked", detail: inv.Error,
                            statusCode: StatusCodes.Status502BadGateway);
                    }
                    return Results.Problem(title: "Upstream error", detail: inv.Error,
                        statusCode: StatusCodes.Status502BadGateway);
                }

                string iri = inv.Value.Value > 0 ? "https://schema.org/InStock" : "https://schema.org/OutOfStock";
                return Results.Json(iri);
            }

            if (cfg.IsOneOff)
            {
                bool isSold = await oneOffStore.IsSoldAsync(chainId, seller, skuNorm, ct);
                string availability = isSold ? "https://schema.org/OutOfStock" : "https://schema.org/InStock";
                return Results.Json(availability);
            }

            return Results.Problem(title: "Not found", detail: "Availability not configured",
                statusCode: StatusCodes.Status404NotFound);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(title: "Invalid request", detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (HttpRequestException ex)
        {
            return Results.Problem(title: "Upstream HTTP error", detail: ex.Message,
                statusCode: StatusCodes.Status502BadGateway);
        }
        catch (IOException ex)
        {
            return Results.Problem(title: "Upstream I/O error", detail: ex.Message,
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> GetInventory(
        long chainId,
        string seller,
        string sku,
        IMarketRouteStore routes,
        IHttpClientFactory http,
        IOneOffSalesStore oneOffStore,
        Circles.Market.Api.Auth.IOutboundServiceAuthProvider authProvider,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (ctx.Request.Headers.ContainsKey(OutboundGuards.HopHeader))
        {
            return Results.Problem(title: "Loop detected", detail: "A proxy loop was detected.",
                statusCode: StatusCodes.Status508LoopDetected);
        }

        try
        {
            seller = Utils.NormalizeAddr(seller);
            string skuNorm = sku.Trim().ToLowerInvariant();

            var cfg = await routes.TryGetAsync(chainId, seller, skuNorm, ct);
            if (cfg is null || !cfg.IsConfigured)
            {
                return Results.Problem(title: "Not found", detail: "Product not configured",
                    statusCode: StatusCodes.Status404NotFound);
            }

            var invUpstream = await routes.TryResolveUpstreamAsync(chainId, seller, skuNorm, MarketServiceKind.Inventory, ct);
            if (string.IsNullOrWhiteSpace(invUpstream))
            {
                if (cfg.IsOneOff)
                {
                    bool isSold = await oneOffStore.IsSoldAsync(chainId, seller, skuNorm, ct);
                    var value = isSold ? 0 : 1;
                    var qv = new SchemaOrgQuantitativeValue
                    {
                        Type = "QuantitativeValue",
                        Value = value
                    };
                    return Results.Json(qv);
                }

                return Results.Problem(title: "Not supported", detail: "Inventory not supported for this offer",
                    statusCode: StatusCodes.Status502BadGateway);
            }

            var inv = await FetchInventoryAsync(http, authProvider, invUpstream!, ct);
            if (inv.IsError)
            {
                if (inv.Error == "Blocked private address")
                {
                    return Results.Problem(title: "Upstream blocked", detail: inv.Error,
                        statusCode: StatusCodes.Status502BadGateway);
                }
                return Results.Problem(title: "Upstream error", detail: inv.Error,
                    statusCode: StatusCodes.Status502BadGateway);
            }

            return Results.Json(inv.Value);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(title: "Invalid request", detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (HttpRequestException ex)
        {
            return Results.Problem(title: "Upstream HTTP error", detail: ex.Message,
                statusCode: StatusCodes.Status502BadGateway);
        }
        catch (IOException ex)
        {
            return Results.Problem(title: "Upstream I/O error", detail: ex.Message,
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<(bool IsError, string? Error, string? Value)> FetchAvailabilityAsync(
        IHttpClientFactory http, Auth.IOutboundServiceAuthProvider authProvider, string url,
        CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !OutboundGuards.IsHttpOrHttps(uri))
        {
            return (true, "Invalid or non-HTTP/HTTPS URL", null);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(OutboundGuards.GetAvailabilityTimeoutMs()));

        var (seller, chain) = TryParseInventoryPath(uri);

        (string headerName, string apiKey)? header = null;
        if (seller != null && chain > 0)
        {
            header = await authProvider.TryGetHeaderAsync(uri, serviceKind: "inventory", sellerAddress: seller,
                chainId: chain, ct: timeoutCts.Token);
        }

        if (header == null && await OutboundGuards.IsPrivateOrLocalTargetAsync(uri, timeoutCts.Token))
        {
            return (true, "Blocked private address", null);
        }

        using var client = http.CreateClient(header != null ? "inventory_trusted" : "inventory_public");

        HttpResponseMessage resp;
        if (header != null)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.TryAddWithoutValidation(OutboundGuards.HopHeader, "1");
            req.Headers.TryAddWithoutValidation(header.Value.headerName, header.Value.apiKey);
            resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
        }
        else
        {
            resp = await OutboundGuards.SendWithRedirectsAsync(client, new HttpRequestMessage(HttpMethod.Get, uri)
            {
                Headers = { { OutboundGuards.HopHeader, "1" } }
            }, OutboundGuards.GetMaxRedirects(), u => new HttpRequestMessage(HttpMethod.Get, u)
            {
                Headers = { { OutboundGuards.HopHeader, "1" } }
            }, timeoutCts.Token);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                return (true, $"HTTP {(int)resp.StatusCode}", null);
            }

            var bytes = await OutboundGuards.ReadWithLimitAsync(resp.Content, OutboundGuards.GetMaxResponseBytes(), timeoutCts.Token);
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.ValueKind != JsonValueKind.String)
            {
                return (true, "availabilityFeed must be a single JSON string", null);
            }

            string? iri = doc.RootElement.GetString();
            if (string.IsNullOrWhiteSpace(iri))
            {
                return (true, "availabilityFeed string is empty", null);
            }

            // best-effort shape check: absolute URI
            if (!Uri.TryCreate(iri, UriKind.Absolute, out _))
            {
                return (true, "availabilityFeed value must be an absolute URI", null);
            }

            return (false, null, iri);
        }
    }

    private static async Task<(bool IsError, string? Error, SchemaOrgQuantitativeValue Value)> FetchInventoryAsync(
        IHttpClientFactory http, Circles.Market.Api.Auth.IOutboundServiceAuthProvider authProvider, string url,
        CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !OutboundGuards.IsHttpOrHttps(uri))
        {
            return (true, "Invalid or non-HTTP/HTTPS URL", default!);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(OutboundGuards.GetInventoryTimeoutMs()));

        var (seller, chain) = TryParseInventoryPath(uri);

        (string headerName, string apiKey)? header = null;
        if (seller != null && chain > 0)
        {
            header = await authProvider.TryGetHeaderAsync(uri, serviceKind: "inventory", sellerAddress: seller,
                chainId: chain, ct: timeoutCts.Token);
        }

        if (header == null && await OutboundGuards.IsPrivateOrLocalTargetAsync(uri, timeoutCts.Token))
        {
            return (true, "Blocked private address", default!);
        }

        using var client = http.CreateClient(header != null ? "inventory_trusted" : "inventory_public");

        HttpResponseMessage resp;
        if (header != null)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.TryAddWithoutValidation(OutboundGuards.HopHeader, "1");
            req.Headers.TryAddWithoutValidation(header.Value.headerName, header.Value.apiKey);
            resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
        }
        else
        {
            resp = await OutboundGuards.SendWithRedirectsAsync(client, new HttpRequestMessage(HttpMethod.Get, uri)
            {
                Headers = { { OutboundGuards.HopHeader, "1" } }
            }, OutboundGuards.GetMaxRedirects(), u => new HttpRequestMessage(HttpMethod.Get, u)
            {
                Headers = { { OutboundGuards.HopHeader, "1" } }
            }, timeoutCts.Token);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                return (true, $"HTTP {(int)resp.StatusCode}", default!);
            }

            var bytes = await OutboundGuards.ReadWithLimitAsync(resp.Content, OutboundGuards.GetMaxResponseBytes(), timeoutCts.Token);
            var el = JsonSerializer.Deserialize<JsonElement>(bytes, Circles.Profiles.Models.JsonSerializerOptions.JsonLd);
            if (el.ValueKind != JsonValueKind.Object)
            {
                return (true, "inventoryFeed must be a QuantitativeValue object", default!);
            }

            try
            {
                var qv = el.Deserialize<SchemaOrgQuantitativeValue>(Circles.Profiles.Models.JsonSerializerOptions.JsonLd)!;
                if (!string.Equals(qv.Type, "QuantitativeValue", StringComparison.Ordinal))
                {
                    return (true, "@type must be QuantitativeValue", default!);
                }

                // value is represented as a long in the model; no fractional part is possible.
                // Keep a simple range/shape validation if needed in the future.

                return (false, null, qv);
            }
            catch (Exception ex)
            {
                return (true, $"Invalid QuantitativeValue: {ex.Message}", default!);
            }
        }
    }

    private static (string? seller, long chainId) TryParseInventoryPath(Uri uri)
    {
        try
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            // expect: (inventory|availability)/{chainId}/{seller}/{sku}
            for (int i = 0; i + 3 < segments.Length; i++)
            {
                bool isInventory = string.Equals(segments[i], "inventory", StringComparison.OrdinalIgnoreCase);
                bool isAvailability = string.Equals(segments[i], "availability", StringComparison.OrdinalIgnoreCase);
                if (!isInventory && !isAvailability)
                {
                    continue;
                }

                bool chainOk = long.TryParse(segments[i + 1], out var chain);
                bool sellerOk = segments[i + 2].StartsWith("0x", StringComparison.OrdinalIgnoreCase) && segments[i + 2].Length == 42;
                if (chainOk && sellerOk)
                {
                    return (segments[i + 2].ToLowerInvariant(), chain);
                }
            }
        }
        catch
        {
        }

        return (null, 0);
    }
}
