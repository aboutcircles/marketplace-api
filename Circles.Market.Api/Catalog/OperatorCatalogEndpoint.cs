using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Circles.Market.Api.Routing;
using Circles.Profiles.Market;
using Circles.Profiles.Models.Market;

namespace Circles.Market.Api.Catalog;

/// <summary>
/// GET /api/operator/{op}/catalog
///
/// Aggregates verified product links across many avatars under the operator namespace,
/// applies a time window and chain domain, reduces to a deterministic AggregatedCatalog,
/// and returns a **paged** JSON-LD object.
///
/// Query:
/// - avatars: repeated query param, at least one (e.g. ?avatars=0x..&avatars=0x..)
/// - chainId: long (default 100)
/// - start: unix seconds inclusive (default 0)
/// - end: unix seconds inclusive (default now)
/// - pageSize: 1..100 (default 20)
/// - cursor: opaque base64 { "start": <int> } (wins over offset)
/// - offset: 0-based (alternative to cursor)
///
/// Response: application/ld+json; charset=utf-8
/// Headers: Link, X-Next-Cursor
/// </summary>
public static class OperatorCatalogEndpoint
{
    public static async Task Handle(
        string op,
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
        ILogger? logger = null)
    {
        try
        {
            var avatars = ctx.Request.Query["avatars"].Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a!).ToArray();

            logger?.LogInformation(
                "Operator catalog request op={Op} avatars={Avatars} chainId={Chain} start={Start} end={End} pageSize={PageSize} cursor={Cursor} offset={Offset}",
                op,
                string.Join(",", avatars),
                chainId, start, end, pageSize, cursor, offset);

            bool hasAvatars = avatars is { Length: > 0 };
            if (!hasAvatars)
            {
                logger?.LogWarning("Bad request: missing avatars parameter");
                await WriteError(ctx, StatusCodes.Status400BadRequest,
                    "At least one avatars query parameter is required");
                return;
            }

            // Enforce configurable cap on avatars to bound work (default 500)
            int maxAvatars = int.TryParse(Environment.GetEnvironmentVariable("CATALOG_MAX_AVATARS"), out var cap) && cap > 0
                ? cap
                : 500;
            if (avatars.Length > maxAvatars)
            {
                logger?.LogWarning("Bad request: avatars exceeds cap {Count} > {Max}", avatars.Length, maxAvatars);
                await WriteError(ctx, StatusCodes.Status400BadRequest, $"avatars must be <= {maxAvatars}");
                return;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long chain = chainId ?? MarketConstants.Defaults.ChainId;
            long winStart = start ?? MarketConstants.Defaults.WindowStart;
            long winEnd = end ?? now;

            bool windowOk = winStart <= winEnd;
            if (!windowOk)
            {
                logger?.LogWarning("Bad request: invalid window start={Start} end={End}", winStart, winEnd);
                await WriteError(ctx, StatusCodes.Status400BadRequest, "start must be <= end");
                return;
            }

            int size = Math.Clamp(pageSize ?? MarketConstants.Defaults.PageSize,
                MarketConstants.Defaults.PageSizeMin,
                MarketConstants.Defaults.PageSizeMax);
            int startIndex = 0;
            if (!string.IsNullOrEmpty(cursor))
            {
                try
                {
                    var payload = Convert.FromBase64String(cursor);
                    var el = JsonSerializer.Deserialize<JsonElement>(payload);
                    startIndex = el.GetProperty("start").GetInt32();
                    if (startIndex < 0)
                    {
                        logger?.LogWarning("Bad request: cursor.start < 0");
                        await WriteError(ctx, StatusCodes.Status400BadRequest, "cursor.start must be >= 0");
                        return;
                    }
                }
                catch
                {
                    logger?.LogWarning("Bad request: invalid cursor format");
                    await WriteError(ctx, StatusCodes.Status400BadRequest, "Invalid cursor");
                    return;
                }
            }
            else if (offset.HasValue)
            {
                if (offset.Value < 0)
                {
                    logger?.LogWarning("Bad request: negative offset {Offset}", offset);
                    await WriteError(ctx, StatusCodes.Status400BadRequest, "offset must be >= 0");
                    return;
                }

                startIndex = offset.Value;
            }

            // Validate operator address early to avoid downstream surprises
            try { op = Utils.NormalizeAddr(op); }
            catch (ArgumentException ex)
            {
                logger?.LogWarning(ex, "Bad request: invalid operator address");
                await WriteError(ctx, StatusCodes.Status400BadRequest, ex.Message);
                return;
            }

            var (avatarsScanned, products, errors) =
                await opCatalog.AggregateAsync(op, avatars, chain, winStart, winEnd, ct);

            // Note: DB routing controls feed emission (see ReplaceInventoryFeedUrlsAsync), not aggregation.
            // All aggregated products are included in the catalog response.

            int total = products.Count;

            bool beyondEnd = startIndex > total;
            if (beyondEnd)
            {
                ctx.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                return;
            }

            int endIndex = Math.Min(startIndex + size, total);
            string? next = endIndex < total
                ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { start = endIndex }))
                : null;
            if (next is not null)
            {
                var basePath = $"/api/operator/{WebUtility.UrlEncode(op)}/catalog";
                string nextUrl = $"{basePath}?pageSize={size}&cursor={WebUtility.UrlEncode(next)}";
                foreach (var av in avatars)
                {
                    nextUrl += $"&avatars={WebUtility.UrlEncode(av)}";
                }

                nextUrl += $"&chainId={chain}&start={winStart}&end={winEnd}";
                ctx.Response.Headers.Append(MarketConstants.Headers.Link, $"<{nextUrl}>; rel=\"next\"");
                ctx.Response.Headers.Append(MarketConstants.Headers.XNextCursor, next);
                logger?.LogDebug("Pagination: next cursor {Cursor} start={Start} size={Size} total={Total}", next,
                    endIndex, size, total);
            }

            ctx.Response.ContentType = MarketConstants.ContentTypes.JsonLdUtf8;

            var page = products.Skip(startIndex).Take(endIndex - startIndex).ToList();

            // Rewrite offer feed URLs to point to the Market API endpoints
            // so clients consume a uniform, validated interface instead of arbitrary upstream URLs.
            string baseUrl = ResolvePublicBaseUrl(ctx);
            var totalInventoryByProductIndex = await ReplaceInventoryFeedUrlsAsync(page, chain, baseUrl, routes, ct);

            var aggPayload = new AggregatedCatalog
            {
                Operator = Utils.NormalizeAddr(op),
                ChainId = chain,
                Window = new AggregatedCatalogWindow { Start = winStart, End = winEnd },
                AvatarsScanned = avatarsScanned,
                Products = page,
                Errors = errors
            };

            await SerializeCatalogWithTotalInventoryAsync(
                ctx.Response.Body,
                aggPayload,
                totalInventoryByProductIndex,
                ct);

            logger?.LogInformation(
                "Operator catalog response: avatarsScanned={AvCount} pageCount={PageCount} totalProducts={Total} errors={ErrCount}",
                avatarsScanned.Count, page.Count, total, errors.Count);
        }
        catch (ArgumentException ex)
        {
            logger?.LogWarning(ex, "Bad request: {Message}", ex.Message);
            await WriteError(ctx, StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            // Common cause: chainId mismatch between request and configured chain
            logger?.LogWarning(ex, "Invalid operation: {Message}", ex.Message);
            await WriteError(ctx, StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (PayloadTooLargeException ex)
        {
            logger?.LogWarning(ex, "Payload too large: {Message}", ex.Message);
            await WriteError(ctx, StatusCodes.Status413PayloadTooLarge, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            logger?.LogWarning(ex, "Upstream HTTP error: {Message}", ex.Message);
            await WriteError(ctx, StatusCodes.Status502BadGateway, ex.Message);
        }
        catch (IOException ex)
        {
            logger?.LogWarning(ex, "I/O error: {Message}", ex.Message);
            await WriteError(ctx, StatusCodes.Status502BadGateway, ex.Message);
        }
    }

    private static async Task<List<long?>> ReplaceInventoryFeedUrlsAsync(
        List<AggregatedCatalogItem> page,
        long chain,
        string baseUrl,
        IMarketRouteStore routes,
        CancellationToken ct)
    {
        var totals = Enumerable.Repeat<long?>(null, page.Count).ToList();
        if (page.Count <= 0)
        {
            return totals;
        }

        for (int i = 0; i < page.Count; i++)
        {
            var item = page[i];
            var prod = item.Product;
            if (prod is null || string.IsNullOrWhiteSpace(prod.Sku) || prod.Offers is not { Count: > 0 })
            {
                continue;
            }

            string sellerAddr = Utils.NormalizeAddr(item.Seller);
            string sku = prod.Sku.Trim().ToLowerInvariant();

            var cfg = await routes.TryGetAsync(chain, sellerAddr, sku, ct);
            totals[i] = cfg?.TotalInventory;

            bool isOneOff = cfg is not null && cfg.IsOneOff;

            string? invUpstream = null;
            string? availUpstream = null;

            if (cfg is not null && cfg.IsConfigured && !cfg.IsOneOff)
            {
                invUpstream = await routes.TryResolveUpstreamAsync(chain, sellerAddr, sku, MarketServiceKind.Inventory, ct);
                availUpstream = await routes.TryResolveUpstreamAsync(chain, sellerAddr, sku, MarketServiceKind.Availability, ct);
            }

            bool hasInventoryRoute = !string.IsNullOrWhiteSpace(invUpstream);
            bool hasAvailabilityRoute = !string.IsNullOrWhiteSpace(availUpstream);

            bool emitAvailability = hasInventoryRoute || hasAvailabilityRoute || isOneOff;
            bool emitInventory = hasInventoryRoute;

            static string Join(string a, string b) => a.EndsWith('/')
                ? (a + (b.StartsWith('/') ? b[1..] : b))
                : (a + (b.StartsWith('/') ? b : "/" + b));

            var newOffers = new List<SchemaOrgOffer>(prod.Offers.Count);

            foreach (var offer in prod.Offers)
            {
                string? availability = emitAvailability
                    ? Join(baseUrl,
                        $"{MarketConstants.Routes.AvailabilityBase}/{chain}/{WebUtility.UrlEncode(sellerAddr)}/{WebUtility.UrlEncode(sku)}")
                    : null;

                string? inventory = emitInventory
                    ? Join(baseUrl,
                        $"{MarketConstants.Routes.InventoryBase}/{chain}/{WebUtility.UrlEncode(sellerAddr)}/{WebUtility.UrlEncode(sku)}")
                    : null;

                var action = new SchemaOrgPayAction
                {
                    Price = offer.Price,
                    PriceCurrency = offer.PriceCurrency,
                    Recipient = offer.PotentialAction?.Recipient,
                    Instrument = offer.PotentialAction?.Instrument
                };

                newOffers.Add(offer with
                {
                    CirclesAvailabilityFeed = availability,
                    CirclesInventoryFeed = inventory,
                    CirclesFulfillmentEndpoint = null,
                    PotentialAction = action
                });
            }

            var newProd = prod with { Offers = newOffers };
            page[i] = item with { Product = newProd };
        }

        return totals;
    }

    private static async Task SerializeCatalogWithTotalInventoryAsync(
        Stream output,
        AggregatedCatalog payload,
        IReadOnlyList<long?> totalInventoryByProductIndex,
        CancellationToken ct)
    {
        JsonNode? root = JsonSerializer.SerializeToNode(payload, Circles.Profiles.Models.JsonSerializerOptions.JsonLd);
        if (root is JsonObject obj &&
            obj["products"] is JsonArray products)
        {
            int count = Math.Min(products.Count, totalInventoryByProductIndex.Count);
            for (int i = 0; i < count; i++)
            {
                var totalInventory = totalInventoryByProductIndex[i];
                if (totalInventory is null)
                {
                    continue;
                }

                if (products[i] is JsonObject itemObj && itemObj["product"] is JsonObject productObj)
                {
                    productObj["totalInventory"] = totalInventory.Value;
                }
            }
        }

        await JsonSerializer.SerializeAsync(output, root, Circles.Profiles.Models.JsonSerializerOptions.JsonLd, ct);
    }

    private static string ResolvePublicBaseUrl(HttpContext ctx)
    {
        // Prefer explicit external/public base URL when running behind a proxy or ingress.
        // Supported env vars (first wins): PUBLIC_BASE_URL, EXTERNAL_BASE_URL
        var explicitBase = Environment.GetEnvironmentVariable("PUBLIC_BASE_URL")
                           ?? Environment.GetEnvironmentVariable("EXTERNAL_BASE_URL");
        if (!string.IsNullOrWhiteSpace(explicitBase))
        {
            return explicitBase!.TrimEnd('/');
        }

        // Optional: allow forcing scheme via env (e.g., set PUBLIC_BASE_SCHEME=http for local dev)
        var forcedScheme = Environment.GetEnvironmentVariable("PUBLIC_BASE_SCHEME")
                           ?? Environment.GetEnvironmentVariable("EXTERNAL_BASE_SCHEME");

        // Fallback: build from the incoming request (scheme://host[:port][/pathBase])
        var host = ctx.Request.Host.HasValue ? ctx.Request.Host.Value : "localhost";

        // Prefer forced scheme > X-Forwarded-Proto > Request.Scheme > https
        string scheme;
        if (!string.IsNullOrWhiteSpace(forcedScheme))
        {
            scheme = forcedScheme!.Trim();
        }
        else if (ctx.Request.Headers.TryGetValue("X-Forwarded-Proto", out var proto) && !string.IsNullOrWhiteSpace(proto.ToString()))
        {
            scheme = proto.ToString();
        }
        else
        {
            scheme = string.IsNullOrWhiteSpace(ctx.Request.Scheme) ? "https" : ctx.Request.Scheme;
        }

        var pathBase = ctx.Request.PathBase.HasValue ? ctx.Request.PathBase.Value : string.Empty;
        return $"{scheme}://{host}{pathBase}".TrimEnd('/');
    }

    private static async Task WriteError(HttpContext ctx, int status, string message, object? details = null)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = MarketConstants.ContentTypes.JsonLdUtf8;
        var payload = JsonSerializer.Serialize(new { error = message, details });
        await ctx.Response.WriteAsync(payload);
    }
}
