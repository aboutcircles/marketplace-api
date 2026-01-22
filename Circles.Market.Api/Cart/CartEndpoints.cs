using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Circles.Market.Api.Cart.Validation;
using Circles.Market.Api.Payments;

namespace Circles.Market.Api.Cart;

public static class CartEndpoints
{
    private const string ContentType = MarketConstants.ContentTypes.JsonLdUtf8;

    public static RouteGroupBuilder MapCartApi(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup(MarketConstants.Routes.CartBase);

        g.MapPost("/baskets", CreateBasket)
            .WithSummary("Create a new basket")
            .WithDescription("Body: { operator, buyer, chainId }")
            .Accepts<BasketCreateRequest>(MarketConstants.ContentTypes.JsonLdUtf8, MarketConstants.ContentTypes.Json)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest);

        g.MapGet("/baskets/{basketId}", GetBasket)
            .WithSummary("Get basket by id")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status410Gone);

        g.MapPatch("/baskets/{basketId}", PatchBasket)
            .WithSummary("Patch basket (merge)")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status410Gone);

        g.MapPost("/baskets/{basketId}/validate", Validate)
            .WithSummary("Validate basket")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status410Gone);

        g.MapPost("/baskets/{basketId}/checkout", Checkout)
            .WithSummary("Checkout basket -> create order id")
            .Produces(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status410Gone);

        g.MapPost("/baskets/{basketId}/preview", Preview)
            .WithSummary("Preview Order snapshot without persisting")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status410Gone);

        // Authenticated: bulk retrieve orders by ids for the authenticated buyer. Invalid/missing IDs are omitted.
        g.MapPost("/orders/batch", GetOrdersBatch)
            .WithSummary("Get multiple orders by ids for the authenticated buyer")
            .Accepts<BulkOrderRequest>(MarketConstants.ContentTypes.JsonLdUtf8, MarketConstants.ContentTypes.Json)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .RequireAuthorization();

        // Authenticated: retrieve orders for the current buyer (from JWT claims)
        g.MapGet("/orders/by-buyer", GetOrdersByBuyer)
            .WithSummary("Get orders for the authenticated buyer address")
            .Produces(StatusCodes.Status200OK)
            .RequireAuthorization();

        // Authenticated: retrieve a single order by id for the current buyer
        g.MapGet("/orders/{orderId}", GetOrderById)
            .WithSummary("Get a single order by id for the authenticated buyer")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization();

        // SELLER: list orders for authenticated seller (sales)
        g.MapGet("/orders/by-seller", GetOrdersBySeller)
            .WithSummary("Get orders (sales) for the authenticated seller address")
            .Produces(StatusCodes.Status200OK)
            .RequireAuthorization();

        // SELLER: get single order for authenticated seller (filtered view)
        g.MapGet("/orders/{orderId}/as-seller", GetOrderByIdAsSeller)
            .WithSummary("Get a single order by id for the authenticated seller (filtered view)")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization();

        // NEW: Authenticated: retrieve status history for a single order (buyer-scoped)
        g.MapGet("/orders/{orderId}/status-history", GetOrderStatusHistory)
            .WithSummary("Get status history for the authenticated buyer")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization();

        // Server-sent events for order status updates for the authenticated buyer
        g.MapGet("/orders/events", SubscribeOrderEvents)
            .WithSummary("Server-sent events for order status updates of the authenticated buyer")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .RequireAuthorization();

        // Server-sent events for order status updates for the authenticated seller (sales)
        g.MapGet("/orders/sales/events", SubscribeSalesEvents)
            .WithSummary("Server-sent events for order status updates of the authenticated seller (sales)")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .RequireAuthorization();

        return g;
    }

    private static async Task<IResult> CreateBasket(HttpContext ctx, IBasketStore store, ILoggerFactory logger)
    {
        ctx.Response.ContentType = ContentType;
        ctx.Response.Headers.Append(MarketConstants.Headers.XContentTypeOptions, MarketConstants.Headers.NoSniff);
        try
        {
            var req = await JsonSerializer.DeserializeAsync<BasketCreateRequest>(ctx.Request.Body,
                Circles.Profiles.Models.JsonSerializerOptions.JsonLd, ctx.RequestAborted) ?? new BasketCreateRequest();
            string? op = req.Operator is null ? null : Utils.NormalizeAddr(req.Operator);
            string? buyer = req.Buyer is null ? null : Utils.NormalizeAddr(req.Buyer);
            long? chain = req.ChainId;
            var b = store.Create(op, buyer, chain);
            var resp = new BasketCreateResponse { BasketId = b.BasketId };
            return Results.Json(resp, Circles.Profiles.Models.JsonSerializerOptions.JsonLd, contentType: ContentType);
        }
        catch (ArgumentException ex)
        {
            return Error(StatusCodes.Status400BadRequest, ex.Message);
        }
    }

    private static IResult NotFoundOrGone((Basket basket, bool expired)? res)
    {
        if (res is null) return Results.StatusCode(StatusCodes.Status404NotFound);
        return res.Value.expired
            ? Results.StatusCode(StatusCodes.Status410Gone)
            : ResultsExtensions.JsonLd(res.Value.basket);
    }

    private static async Task<IResult> GetBasket(string basketId, IBasketStore store)
    {
        var res = store.Get(basketId);
        if (res is null) return Results.StatusCode(StatusCodes.Status404NotFound);
        if (res.Value.expired) return Results.StatusCode(StatusCodes.Status410Gone);
        return ResultsExtensions.JsonLd(res.Value.basket);
    }

    private class BasketPatch
    {
        [JsonPropertyName("items")] public List<OrderItemPreview>? Items { get; set; }
        [JsonPropertyName("customer")] public PersonMinimal? Customer { get; set; }
        [JsonPropertyName("shippingAddress")] public PostalAddress? ShippingAddress { get; set; }
        [JsonPropertyName("billingAddress")] public PostalAddress? BillingAddress { get; set; }
        [JsonPropertyName("ageProof")] public PersonMinimal? AgeProof { get; set; }
        [JsonPropertyName("contactPoint")] public ContactPoint? ContactPoint { get; set; }
        [JsonPropertyName("ttlSeconds")] public int? TtlSeconds { get; set; }
    }

    private static readonly HashSet<string> AllowedPatchFields = new(StringComparer.Ordinal)
    {
        "items", "customer", "shippingAddress", "billingAddress", "ageProof", "contactPoint", "ttlSeconds"
    };

    private static async Task<IResult> PatchBasket(string basketId, HttpContext ctx, IBasketStore store, IBasketCanonicalizer canonicalizer)
    {
        ctx.Response.ContentType = ContentType;
        ctx.Response.Headers.Append("X-Content-Type-Options", "nosniff");

        // Enforce whitelist of top-level fields
        using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (!AllowedPatchFields.Contains(prop.Name))
            {
                return Error(StatusCodes.Status400BadRequest, $"Unknown field '{prop.Name}'",
                    new { path = "/" + prop.Name });
            }
        }

        try
        {
            // Deserialize after validation; malformed shapes may throw JsonException
            var patch = doc.RootElement.Deserialize<BasketPatch>(Profiles.Models.JsonSerializerOptions.JsonLd) ??
                        new BasketPatch();

            var res = store.Get(basketId);
            if (res is null) return Results.StatusCode(StatusCodes.Status404NotFound);
            if (res.Value.expired) return Results.StatusCode(StatusCodes.Status410Gone);

            var b = res.Value.basket;
            if (string.Equals(b.Status, nameof(BasketStatus.CheckedOut), StringComparison.Ordinal))
            {
                return Results.StatusCode(StatusCodes.Status409Conflict);
            }

            // Apply patch locally (no persistence yet)
            if (patch.TtlSeconds.HasValue)
            {
                int v = patch.TtlSeconds.Value;
                const int MinTtl = 1;
                const int MaxTtl = 604800; // 7 days
                if (v < MinTtl || v > MaxTtl)
                {
                    return Error(StatusCodes.Status400BadRequest, $"ttlSeconds must be in range [{MinTtl}, {MaxTtl}]");
                }
                b.TtlSeconds = v;
            }
            if (patch.Items is not null)
            {
                if (patch.Items.Count > 500)
                    throw new ArgumentException("items.length must be <= 500");
                foreach (var it in patch.Items)
                {
                    if (it.OrderQuantity <= 0 || it.OrderQuantity > 1_000_000)
                        throw new ArgumentException("orderQuantity out of bounds [1, 1_000_000]");
                    if (!string.IsNullOrEmpty(it.Seller)) it.Seller = Utils.NormalizeAddr(it.Seller!);

                    // Never use client-provided offerSnapshot
                    it.OfferSnapshot = null;
                }

                b.Items = patch.Items;
            }

            if (patch.ShippingAddress is not null) b.ShippingAddress = patch.ShippingAddress;
            if (patch.BillingAddress is not null) b.BillingAddress = patch.BillingAddress;
            if (patch.Customer is not null) b.Customer = patch.Customer;
            if (patch.AgeProof is not null) b.AgeProof = patch.AgeProof;
            if (patch.ContactPoint is not null) b.ContactPoint = patch.ContactPoint;

            // Canonicalize basket so stored data is trustworthy
            await canonicalizer.CanonicalizeAsync(b, ctx.RequestAborted);

            // Persist canonicalized values in a single write and use the returned basket (fresh ModifiedAt/Version)
            var persisted = store.Patch(basketId, bb =>
            {
                bb.Items = b.Items;
                bb.ShippingAddress = b.ShippingAddress;
                bb.BillingAddress = b.BillingAddress;
                bb.Customer = b.Customer;
                bb.AgeProof = b.AgeProof;
                bb.ContactPoint = b.ContactPoint;
                bb.TtlSeconds = b.TtlSeconds;
            });

            return ResultsExtensions.JsonLd(persisted);
        }
        catch (JsonException ex)
        {
            return Error(StatusCodes.Status400BadRequest, "Invalid basket JSON", new { path = "$", detail = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return Error(StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (KeyNotFoundException)
        {
            return Results.StatusCode(StatusCodes.Status404NotFound);
        }
        catch (InvalidOperationException ex) when (ex.Message == "Basket already checked out")
        {
            return Results.StatusCode(StatusCodes.Status409Conflict);
        }
        catch (InvalidOperationException ex)
        {
            // Product resolution issues → 422
            return Error(StatusCodes.Status422UnprocessableEntity, ex.Message);
        }
    }

    private static async Task<IResult> Validate(string basketId, IBasketStore store, ICartValidator validator, IBasketCanonicalizer canonicalizer)
    {
        try
        {
            var (basket, vr) = await CanonicalizeAndValidate(basketId, buyer: null, store, validator, canonicalizer);
            return ResultsExtensions.JsonLd(vr);
        }
        catch (KeyNotFoundException)
        {
            return Results.StatusCode(StatusCodes.Status404NotFound);
        }
        catch (InvalidOperationException ex) when (ex.Message == "expired")
        {
            return Results.StatusCode(StatusCodes.Status410Gone);
        }
        catch (InvalidOperationException ex) when (ex.Message == "checked_out")
        {
            return Results.StatusCode(StatusCodes.Status409Conflict);
        }
        catch (ArgumentException ex)
        {
            return Error(StatusCodes.Status422UnprocessableEntity, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Error(StatusCodes.Status422UnprocessableEntity, ex.Message);
        }
    }

    private static async Task<IResult> Preview(
        string basketId,
        string? buyer,
        IBasketStore store,
        ICartValidator validator,
        IBasketCanonicalizer canonicalizer)
    {
        try
        {
            var (basket, vr) = await CanonicalizeAndValidate(basketId, buyer, store, validator, canonicalizer);
            if (!vr.Valid)
            {
                return Results.Json(
                    new { error = "invalid basket", validation = vr },
                    contentType: ContentType,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var order = ComposeOrder(basket, NewId("ord_"), basket.Buyer ?? buyer);
            return ResultsExtensions.JsonLd(order);
        }
        catch (KeyNotFoundException)
        {
            return Results.StatusCode(StatusCodes.Status404NotFound);
        }
        catch (InvalidOperationException ex) when (ex.Message == "expired")
        {
            return Results.StatusCode(StatusCodes.Status410Gone);
        }
        catch (InvalidOperationException ex) when (ex.Message == "checked_out")
        {
            return Results.StatusCode(StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> Checkout(
        string basketId,
        string? buyer,
        IBasketStore store,
        ICartValidator validator,
        IBasketCanonicalizer canonicalizer,
        IOrderStore orderStore)
    {
        try
        {
            var (basket, vr) = await CanonicalizeAndValidate(basketId, buyer, store, validator, canonicalizer);
            bool isValid = vr.Valid;
            if (!isValid)
            {
                return Results.Json(
                    new { error = "invalid basket", validation = vr },
                    contentType: ContentType,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // Atomically freeze and read the snapshot used to compose the order
            var frozen = store.TryFreezeAndRead(basketId, basket.Version);
            if (frozen is null)
            {
                return Results.StatusCode(StatusCodes.Status409Conflict);
            }

            string orderId = NewId(MarketConstants.IdPrefixes.Order);
            string paymentReference = NewId(MarketConstants.IdPrefixes.PaymentReference);
            var order = ComposeOrder(frozen, orderId, frozen.Buyer ?? buyer);
            order.PaymentReference = paymentReference;

            bool created = orderStore.Create(orderId, basketId, order);
            bool duplicateOrder = !created;
            if (duplicateOrder)
            {
                // Extremely unlikely (orderId collision), but better to surface a clear error.
                return Error(StatusCodes.Status409Conflict, "Order already exists");
            }

            // Return orderId as the public identifier (non-secret) + paymentReference
            var payload = new { orderId, basketId, paymentReference, orderCid = (string?)null };
            return Results.Json(
                payload,
                Profiles.Models.JsonSerializerOptions.JsonLd,
                contentType: ContentType,
                statusCode: StatusCodes.Status201Created);
        }
        catch (OneOffAlreadySoldException ex)
        {
            return Error(
                StatusCodes.Status409Conflict,
                "One-off item already sold",
                new { chainId = ex.ChainId, seller = ex.Seller, sku = ex.Sku });
        }
        catch (KeyNotFoundException)
        {
            return Results.StatusCode(StatusCodes.Status404NotFound);
        }
        catch (InvalidOperationException ex) when (ex.Message == "expired")
        {
            return Results.StatusCode(StatusCodes.Status410Gone);
        }
        catch (InvalidOperationException ex) when (ex.Message == "checked_out")
        {
            return Results.StatusCode(StatusCodes.Status409Conflict);
        }
        catch (ArgumentException ex)
        {
            return Error(StatusCodes.Status422UnprocessableEntity, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Error(StatusCodes.Status422UnprocessableEntity, ex.Message);
        }
    }

    private static async Task<(Basket basket, ValidationResult validation)> CanonicalizeAndValidate(
        string basketId,
        string? buyer,
        IBasketStore store,
        ICartValidator validator,
        IBasketCanonicalizer canonicalizer)
    {
        var res = store.Get(basketId);
        if (res is null) throw new KeyNotFoundException();
        if (res.Value.expired) throw new InvalidOperationException("expired");

        var basket = res.Value.basket;
        if (string.Equals(basket.Status, nameof(BasketStatus.CheckedOut), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("checked_out");
        }

        await canonicalizer.CanonicalizeAsync(basket);
        // Patch bumps version — use returned basket so Version is current.
        basket = store.Patch(basketId, b => b.Items = basket.Items);

        var vr = validator.Validate(basket);
        return (basket, vr);
    }

    private static OrderSnapshot ComposeOrder(Basket b, string orderId, string? buyer)
    {
        var order = new OrderSnapshot
        {
            // Public order identifier is the orderId (ord_...)
            OrderNumber = orderId,
            OrderDate = DateTimeOffset.UtcNow.ToString("O"),
            Customer = new SchemaOrgPersonId
            {
                Id = buyer is null ? null : $"eip155:{b.ChainId}:{buyer}",
                GivenName = b.Customer?.GivenName,
                FamilyName = b.Customer?.FamilyName
            },
            Broker = new SchemaOrgOrgId { Id = b.Operator is null ? null : $"eip155:{b.ChainId}:{b.Operator}" },
            BillingAddress = b.BillingAddress,
            ShippingAddress = b.ShippingAddress,
        };
        foreach (var it in b.Items)
        {
            if (it.OfferSnapshot is not null) order.AcceptedOffer.Add(it.OfferSnapshot);
            order.OrderedItem.Add(new OrderItemLine
            {
                OrderQuantity = it.OrderQuantity,
                OrderedItem = it.OrderedItem,
                ProductCid = it.ProductCid
            });
        }

        // Calculate total taking into account the quantity of each ordered item.
        decimal total = 0m;
        string? currency = null;
        for (int i = 0; i < b.Items.Count; i++)
        {
            var srcItem = b.Items[i];
            var offer = srcItem.OfferSnapshot;
            if (offer?.Price is not null)
            {
                var qty = srcItem.OrderQuantity <= 0 ? 1 : srcItem.OrderQuantity;
                total += offer.Price.Value * (decimal)qty;
            }
            if (currency is null && offer?.PriceCurrency is not null)
            {
                currency = offer.PriceCurrency;
            }
        }

        order.TotalPaymentDue = new PriceSpecification { Price = total, PriceCurrency = currency };
        return order;
    }

    // Cryptographically secure 128-bit ID, URL-safe (hex) and prefixed (e.g., ord_ + 32 hex chars)
    private static string NewId(string prefix)
    {
        Span<byte> buf = stackalloc byte[16]; // 128-bit
        RandomNumberGenerator.Fill(buf);
        string token = Convert.ToHexString(buf); // 32 uppercase hex chars
        return prefix + token;
    }


    private sealed class BulkOrderRequest
    {
        // Canonical name only
        [JsonPropertyName("orderIds")] public List<string>? OrderIds { get; set; }
    }

    // POST /orders/batch (authorized; items filtered to caller-owned orders)
    private static async Task<IResult> GetOrdersBatch(HttpContext ctx, IOrderAccessService access)
    {
        ctx.Response.ContentType = ContentType;
        ctx.Response.Headers.Append(MarketConstants.Headers.XContentTypeOptions, MarketConstants.Headers.NoSniff);
        try
        {
            var req = await JsonSerializer.DeserializeAsync<BulkOrderRequest>(ctx.Request.Body,
                Circles.Profiles.Models.JsonSerializerOptions.JsonLd, ctx.RequestAborted) ?? new BulkOrderRequest();

            var ids = req.OrderIds ?? new List<string>();
            if (ids.Count == 0)
                return Error(StatusCodes.Status400BadRequest, "orderIds must be a non-empty array");

            int max = MarketConstants.Defaults.PageSizeMax;
            if (ids.Count > max)
                return Error(StatusCodes.Status400BadRequest, $"orderIds length must be <= {max}");

            string? addr = ctx.User.FindFirst("addr")?.Value;
            string? chainStr = ctx.User.FindFirst("chainId")?.Value;
            if (string.IsNullOrWhiteSpace(addr) || string.IsNullOrWhiteSpace(chainStr) || !long.TryParse(chainStr, out var chainId))
            {
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }

            string pattern = "^" + Regex.Escape(MarketConstants.IdPrefixes.Order) + "[0-9A-F]{32}$";
            var items = new List<OrderSnapshot>(ids.Count);
            foreach (var id in ids)
            {
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (!Regex.IsMatch(id, pattern, RegexOptions.CultureInvariant)) continue;
                var order = await access.GetOrderForBuyerAsync(id, addr!, chainId, ctx.RequestAborted);
                if (order is not null) items.Add(order);
            }

            return ResultsExtensions.JsonLd(new { items });
        }
        catch (JsonException ex)
        {
            return Error(StatusCodes.Status400BadRequest, "Invalid JSON", new { path = "$", detail = ex.Message });
        }
    }

    // GET /orders/by-buyer (authorized)
    private static IResult GetOrdersByBuyer(HttpContext ctx, IOrderAccessService access)
    {
        string? addr = ctx.User.FindFirst("addr")?.Value;
        string? chainStr = ctx.User.FindFirst("chainId")?.Value;
        if (string.IsNullOrWhiteSpace(addr) || string.IsNullOrWhiteSpace(chainStr) || !long.TryParse(chainStr, out var chainId))
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        int page = 1;
        int pageSize = MarketConstants.Defaults.PageSize;
        if (int.TryParse(ctx.Request.Query["page"], out var p) && p > 0) page = p;
        if (int.TryParse(ctx.Request.Query["pageSize"], out var ps)) pageSize = Math.Clamp(ps, MarketConstants.Defaults.PageSizeMin, MarketConstants.Defaults.PageSizeMax);

        var items = access.GetOrdersForBuyerAsync(addr, chainId, page, pageSize).GetAwaiter().GetResult();
        return ResultsExtensions.JsonLd(new { items });
    }

    // GET /orders/{orderId} (authorized)
    private static IResult GetOrderById(string orderId, HttpContext ctx, IOrderAccessService access)
    {
        string pattern = "^" + Regex.Escape(MarketConstants.IdPrefixes.Order) + "[0-9A-F]{32}$";
        if (!Regex.IsMatch(orderId, pattern, RegexOptions.CultureInvariant))
            return Results.StatusCode(StatusCodes.Status404NotFound);

        string? addr = ctx.User.FindFirst("addr")?.Value;
        string? chainStr = ctx.User.FindFirst("chainId")?.Value;
        if (string.IsNullOrWhiteSpace(addr) || string.IsNullOrWhiteSpace(chainStr) || !long.TryParse(chainStr, out var chainId))
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        var order = access.GetOrderForBuyerAsync(orderId, addr!, chainId).GetAwaiter().GetResult();
        return order is null
            ? Results.StatusCode(StatusCodes.Status404NotFound)
            : ResultsExtensions.JsonLd(order);
    }

    // SELLER handlers
    private static IResult GetOrdersBySeller(HttpContext ctx, IOrderAccessService access)
    {
        string? addr = ctx.User.FindFirst("addr")?.Value;
        string? chainStr = ctx.User.FindFirst("chainId")?.Value;
        if (string.IsNullOrWhiteSpace(addr) || string.IsNullOrWhiteSpace(chainStr) || !long.TryParse(chainStr, out var chainId))
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        int page = 1;
        int pageSize = MarketConstants.Defaults.PageSize;
        if (int.TryParse(ctx.Request.Query["page"], out var p) && p > 0) page = p;
        if (int.TryParse(ctx.Request.Query["pageSize"], out var ps)) pageSize = Math.Clamp(ps, MarketConstants.Defaults.PageSizeMin, MarketConstants.Defaults.PageSizeMax);

        var items = access.GetOrdersForSellerAsync(addr, chainId, page, pageSize).GetAwaiter().GetResult();
        return ResultsExtensions.JsonLd(new { items });
    }

    private static IResult GetOrderByIdAsSeller(string orderId, HttpContext ctx, IOrderAccessService access)
    {
        string pattern = "^" + Regex.Escape(MarketConstants.IdPrefixes.Order) + "[0-9A-F]{32}$";
        if (!Regex.IsMatch(orderId, pattern, RegexOptions.CultureInvariant))
            return Results.StatusCode(StatusCodes.Status404NotFound);

        string? addr = ctx.User.FindFirst("addr")?.Value;
        string? chainStr = ctx.User.FindFirst("chainId")?.Value;
        if (string.IsNullOrWhiteSpace(addr) || string.IsNullOrWhiteSpace(chainStr) || !long.TryParse(chainStr, out var chainId))
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        var dto = access.GetOrderForSellerAsync(orderId, addr!, chainId).GetAwaiter().GetResult();
        return dto is null
            ? Results.StatusCode(StatusCodes.Status404NotFound)
            : ResultsExtensions.JsonLd(dto);
    }

    // GET /orders/{orderId}/status-history (authorized)
    private static IResult GetOrderStatusHistory(string orderId, HttpContext ctx, IOrderAccessService access)
    {
        string pattern = "^" + Regex.Escape(MarketConstants.IdPrefixes.Order) + "[0-9A-F]{32}$";
        if (!Regex.IsMatch(orderId, pattern, RegexOptions.CultureInvariant))
            return Results.StatusCode(StatusCodes.Status404NotFound);

        string? addr = ctx.User.FindFirst("addr")?.Value;
        string? chainStr = ctx.User.FindFirst("chainId")?.Value;
        if (string.IsNullOrWhiteSpace(addr) || string.IsNullOrWhiteSpace(chainStr) || !long.TryParse(chainStr, out var chainId))
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        var history = access.GetOrderStatusHistoryForBuyerAsync(orderId, addr!, chainId, ctx.RequestAborted)
            .GetAwaiter().GetResult();

        if (history is null)
        {
            return Results.StatusCode(StatusCodes.Status404NotFound);
        }

        return ResultsExtensions.JsonLd(history);
    }

    private static IResult Error(int status, string message, object? details = null)
        => ResultsExtensions.JsonLd(new { error = message, details }, status);

    // GET /orders/events (authorized SSE)
    private static async Task SubscribeOrderEvents(HttpContext ctx, IOrderStatusEventBus bus, CancellationToken ct)
    {
        string? addr = ctx.User.FindFirst("addr")?.Value;
        string? chainStr = ctx.User.FindFirst("chainId")?.Value;
        if (string.IsNullOrWhiteSpace(addr) || string.IsNullOrWhiteSpace(chainStr) || !long.TryParse(chainStr, out var chainId))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        ctx.Response.Headers["Content-Type"] = "text/event-stream";
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no"; // nginx: disable buffering
        await ctx.Response.Body.FlushAsync(ct);

        await foreach (var evt in bus.SubscribeAsync(addr.ToLowerInvariant(), chainId, ct))
        {
            if (ct.IsCancellationRequested) break;

            var payload = new
            {
                orderId = evt.OrderId,
                paymentReference = evt.PaymentReference,
                oldStatus = evt.OldStatus,
                newStatus = evt.NewStatus,
                changedAt = evt.ChangedAt
            };

            string json = System.Text.Json.JsonSerializer.Serialize(payload);
            await ctx.Response.WriteAsync("event: order-status\n", ct);
            await ctx.Response.WriteAsync($"data: {json}\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);
        }
    }

    // GET /orders/sales/events (authorized SSE for sellers)
    private static async Task SubscribeSalesEvents(HttpContext ctx, IOrderStatusEventBus bus, CancellationToken ct)
    {
        string? addr = ctx.User.FindFirst("addr")?.Value;
        string? chainStr = ctx.User.FindFirst("chainId")?.Value;
        if (string.IsNullOrWhiteSpace(addr) || string.IsNullOrWhiteSpace(chainStr) || !long.TryParse(chainStr, out var chainId))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        ctx.Response.Headers["Content-Type"] = "text/event-stream";
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no"; // nginx: disable buffering
        await ctx.Response.Body.FlushAsync(ct);

        await foreach (var evt in bus.SubscribeForSellerAsync(addr.ToLowerInvariant(), chainId, ct))
        {
            if (ct.IsCancellationRequested) break;

            var payload = new
            {
                orderId = evt.OrderId,
                paymentReference = evt.PaymentReference,
                oldStatus = evt.OldStatus,
                newStatus = evt.NewStatus,
                changedAt = evt.ChangedAt
            };

            string json = System.Text.Json.JsonSerializer.Serialize(payload);
            await ctx.Response.WriteAsync("event: order-status\n", ct);
            await ctx.Response.WriteAsync($"data: {json}\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);
        }
    }
}

internal static class ResultsExtensions
{
    public static IResult JsonLd(object payload, int statusCode = StatusCodes.Status200OK)
        => Results.Json(payload, Circles.Profiles.Models.JsonSerializerOptions.JsonLd,
            contentType: MarketConstants.ContentTypes.JsonLdUtf8, statusCode: statusCode);
}
