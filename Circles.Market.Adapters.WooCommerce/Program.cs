using System.Text.Json;
using System.Text.RegularExpressions;
using Circles.Market.Adapters.WooCommerce;
using Circles.Market.Adapters.WooCommerce.Admin;
using Circles.Market.Adapters.WooCommerce.Auth;
using Circles.Market.Adapters.WooCommerce.Db;
using Circles.Market.Fulfillment.Core;
using Circles.Market.Shared.Admin;
using Circles.Market.Shared.Auth;
using Npgsql;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Prometheus;

var publicBuilder = WebApplication.CreateBuilder(args);

// ── Configuration ─────────────────────────────────────────────────────────────

string? connString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION");
if (string.IsNullOrWhiteSpace(connString))
{
    throw new InvalidOperationException("Missing required POSTGRES_CONNECTION environment variable.");
}

// ── Service Registration ──────────────────────────────────────────────────────

publicBuilder.Services.AddSingleton<WooCommerceDbBootstrapper>(sp =>
    new WooCommerceDbBootstrapper(connString, sp.GetRequiredService<ILogger<WooCommerceDbBootstrapper>>()));

publicBuilder.Services.AddSingleton<ITrustedCallerAuth>(sp =>
    new EnvTrustedCallerAuth(sp.GetRequiredService<ILogger<EnvTrustedCallerAuth>>()));

publicBuilder.Services.AddSingleton<IWooCommerceConnectionResolver>(sp =>
    new PostgresWooCommerceConnectionResolver(connString, sp.GetRequiredService<ILogger<PostgresWooCommerceConnectionResolver>>()));

publicBuilder.Services.AddSingleton<IProductMappingResolver>(sp =>
    new PostgresProductMappingResolver(connString, sp.GetRequiredService<ILogger<PostgresProductMappingResolver>>()));

publicBuilder.Services.AddSingleton<IWooCommerceFulfillmentRunStore>(sp =>
    new PostgresWooCommerceFulfillmentRunStore(connString, sp.GetRequiredService<ILogger<PostgresWooCommerceFulfillmentRunStore>>()));

publicBuilder.Services.AddSingleton<IFulfillmentRunStore>(sp => sp.GetRequiredService<IWooCommerceFulfillmentRunStore>());

publicBuilder.Services.AddSingleton<IInventoryStockStore>(sp =>
    new PostgresInventoryStockStore(connString, sp.GetRequiredService<ILogger<PostgresInventoryStockStore>>()));

publicBuilder.Services.AddHttpClient();

publicBuilder.Services.AddMemoryCache();

var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
if (!string.IsNullOrEmpty(otlpEndpoint))
{
    publicBuilder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService(
            serviceName: Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ?? "market-adapter-woocommerce",
            serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown"))
        .WithTracing(tracing => tracing
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddOtlpExporter());
}

var publicApp = publicBuilder.Build();

// ── Schema Bootstrap ──────────────────────────────────────────────────────────

using (var scope = publicApp.Services.CreateScope())
{
    var bootstrapper = scope.ServiceProvider.GetRequiredService<WooCommerceDbBootstrapper>();
    await bootstrapper.EnsureSchemaAsync();
}

// ── Helpers ──────────────────────────────────────────────────────────────────

async Task<IResult?> AuthorizeAsync(HttpContext context, ITrustedCallerAuth auth,
    string scope, long chainId, string seller, ILogger logger, CancellationToken ct)
{
    string? apiKey = context.Request.Headers["X-Circles-Service-Key"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        logger.LogWarning("AuthorizeAsync: Missing X-Circles-Service-Key for scope={Scope} seller={Seller}", scope, seller);
    }
    var result = await auth.AuthorizeAsync(apiKey, scope, chainId, seller, ct);
    if (!result.Allowed)
    {
        logger.LogWarning("AuthorizeAsync: Denied for scope={Scope} seller={Seller}. Reason={Reason}", scope, seller, result.Reason);
        return Results.Json(new { error = "Unauthorized", reason = result.Reason }, statusCode: 401);
    }
    return null;
}

async Task<(WooCommerceClient? Client, WooCommerceConnectionInfo? Info, IResult? Error)> ResolveWooCommerceClientAsync(
    long chainId, string seller, IWooCommerceConnectionResolver resolver, IHttpClientFactory httpFactory,
    ILoggerFactory loggerFactory, CancellationToken ct)
{
    var info = await resolver.ResolveAsync(chainId, seller, ct);
    if (info == null)
        return (null, null, Results.Json(new { error = "No WooCommerce connection configured for this seller/chain." }, statusCode: 404));

    var settings = new WooCommerceSettings
    {
        BaseUrl = info.BaseUrl,
        ConsumerKey = info.ConsumerKey,
        ConsumerSecret = info.ConsumerSecret,
        TimeoutMs = info.TimeoutMs,
        DefaultCustomerId = info.DefaultCustomerId
    };

    var http = httpFactory.CreateClient();
    var client = new WooCommerceClient(http, settings, loggerFactory.CreateLogger<WooCommerceClient>());
    return (client, info, null);
}

// ── Listening port ───────────────────────────────────────────────────────────

var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
if (string.IsNullOrWhiteSpace(urls))
{
    string? portStr = Environment.GetEnvironmentVariable("PORT");
    int port = 5679; // default WooCommerce adapter port
    if (int.TryParse(portStr, out var p) && p > 0 && p <= 65535)
        port = p;
    publicApp.Urls.Add($"http://0.0.0.0:{port}");
}
else
{
    foreach (var u in urls.Split(';', StringSplitOptions.RemoveEmptyEntries))
        publicApp.Urls.Add(u.Trim());
}

publicApp.UseHttpMetrics();

// ── Health ───────────────────────────────────────────────────────────────────

publicApp.MapGet("/health", async (CancellationToken ct) =>
{
    var checks = new Dictionary<string, string>();
    bool allOk = true;
    try
    {
        await using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1";
        await cmd.ExecuteScalarAsync(ct);
        checks["postgres"] = "ok";
    }
    catch
    {
        checks["postgres"] = "failed";
        allOk = false;
    }
    return Results.Json(new { ok = allOk, checks }, statusCode: allOk ? 200 : 503);
});

// ── GET /inventory/{chainId}/{seller}/{sku} ──────────────────────────────────

publicApp.MapGet("/inventory/{chainId}/{seller}/{sku}", async (
    HttpContext context,
    long chainId,
    string seller,
    string sku,
    ITrustedCallerAuth auth,
    IWooCommerceConnectionResolver connResolver,
    IProductMappingResolver mapper,
    IInventoryStockStore stockStore,
    IHttpClientFactory httpFactory,
    ILoggerFactory loggerFactory,
    CancellationToken ct) =>
{
    var log = loggerFactory.CreateLogger("Inventory");
    var authError = await AuthorizeAsync(context, auth, "inventory", chainId, seller, log, ct);
    if (authError != null) return authError;

    var mapping = await mapper.ResolveAsync(chainId, seller, sku, ct);
    if (mapping == null)
        return Results.NotFound(new { error = "No product mapping found for the requested seller/sku.", sellerAddress = seller, sku });

    // Check local stock override first
    var localQty = await stockStore.GetAvailableQtyAsync(chainId, seller, sku, ct);
    if (localQty.HasValue)
    {
        long value = localQty.Value == -1 ? int.MaxValue : Math.Max(0, localQty.Value);
        return Results.Json(new Dictionary<string, object?> { ["@type"] = "QuantitativeValue", ["value"] = value, ["unitCode"] = "C62" });
    }

    var (wcClient, _, wcError) = await ResolveWooCommerceClientAsync(chainId, seller, connResolver, httpFactory, loggerFactory, ct);
    if (wcError != null) return wcError;

    try
    {
        var product = await wcClient!.GetProductBySkuAsync(mapping.WcProductSku, ct);
        if (product == null)
            return Results.NotFound(new { error = "WooCommerce product not found.", wcProductSku = mapping.WcProductSku });

        int qty = product.StockQuantity ?? 0;
        if (qty < 0) qty = 0;
        return Results.Json(new Dictionary<string, object?> { ["@type"] = "QuantitativeValue", ["value"] = qty, ["unitCode"] = "C62" });
    }
    catch (WooCommerceApiException ex)
    {
        log.LogError(ex, "WooCommerce API error fetching stock for {Seller} {Sku}", seller, sku);
        return Results.Json(new { error = "WooCommerce API error", details = ex.Message }, statusCode: 502);
    }
});

// ── GET /availability/{chainId}/{seller}/{sku} ───────────────────────────────

publicApp.MapGet("/availability/{chainId}/{seller}/{sku}", async (
    HttpContext context,
    long chainId,
    string seller,
    string sku,
    ITrustedCallerAuth auth,
    IWooCommerceConnectionResolver connResolver,
    IProductMappingResolver mapper,
    IInventoryStockStore stockStore,
    IHttpClientFactory httpFactory,
    ILoggerFactory loggerFactory,
    CancellationToken ct) =>
{
    var log = loggerFactory.CreateLogger("Availability");
    var authError = await AuthorizeAsync(context, auth, "inventory", chainId, seller, log, ct);
    if (authError != null) return authError;

    var mapping = await mapper.ResolveAsync(chainId, seller, sku, ct);
    if (mapping == null)
        return Results.NotFound(new { error = "No product mapping found.", sellerAddress = seller, sku });

    // Check local stock override
    var localQty = await stockStore.GetAvailableQtyAsync(chainId, seller, sku, ct);
    if (localQty.HasValue)
    {
        string availability = (localQty.Value > 0 || localQty.Value == -1) ? "https://schema.org/InStock" : "https://schema.org/OutOfStock";
        return Results.Json(availability);
    }

    var (wcClient, _, wcError) = await ResolveWooCommerceClientAsync(chainId, seller, connResolver, httpFactory, loggerFactory, ct);
    if (wcError != null) return wcError;

    try
    {
        var product = await wcClient!.GetProductBySkuAsync(mapping.WcProductSku, ct);
        if (product == null)
            return Results.NotFound(new { error = "WooCommerce product not found." });

        bool inStock = product.StockQuantity > 0
            || product.StockStatus?.Equals("instock", StringComparison.OrdinalIgnoreCase) == true;
        string availability = inStock ? "https://schema.org/InStock" : "https://schema.org/OutOfStock";
        return Results.Json(availability);
    }
    catch (WooCommerceApiException ex)
    {
        log.LogError(ex, "WooCommerce API error checking availability for {Seller} {Sku}", seller, sku);
        return Results.Json(new { error = "WooCommerce API error", details = ex.Message }, statusCode: 502);
    }
});

// ── POST /fulfill/{chainId:long}/{seller} ───────────────────────────────────

publicApp.MapPost("/fulfill/{chainId:long}/{seller}", async (
    long chainId,
    string seller,
    HttpContext context,
    ITrustedCallerAuth auth,
    IWooCommerceConnectionResolver connResolver,
    IProductMappingResolver mapper,
    IInventoryStockStore stockStore,
    IWooCommerceFulfillmentRunStore runStore,
    IHostApplicationLifetime lifetime,
    IHttpClientFactory httpFactory,
    ILoggerFactory loggerFactory,
    CancellationToken ct) =>
{
    var log = loggerFactory.CreateLogger("Fulfill");

    static bool IsValidSeller(string s) =>
        !string.IsNullOrWhiteSpace(s) && Regex.IsMatch(s.Trim().ToLowerInvariant(), "^0x[0-9a-f]{40}$");

    if (!IsValidSeller(seller))
        return Results.BadRequest(new { error = "Invalid seller address" });

    var authError = await AuthorizeAsync(context, auth, "fulfill", chainId, seller, loggerFactory.CreateLogger("Auth"), ct);
    if (authError != null) return authError;

    // Parse request
    WooCommerceFulfillmentRequest? req;
    try
    {
        req = await JsonSerializer.DeserializeAsync<WooCommerceFulfillmentRequest>(context.Request.Body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
    }
    catch (Exception ex)
    {
        log.LogWarning(ex, "Failed to parse fulfill request body");
        return Results.BadRequest(new { error = "Invalid JSON body" });
    }

    if (req == null || string.IsNullOrWhiteSpace(req.PaymentReference) || string.IsNullOrWhiteSpace(req.OrderId))
        return Results.BadRequest(new { error = "orderId and paymentReference are required" });

    string sellerLower = seller.Trim().ToLowerInvariant();

    // Idempotency: try to insert with explicit idempotency_key
    Guid idempotencyKey = req.IdempotencyKey ?? Guid.NewGuid();
    Guid runId;

    try
    {
        runId = await runStore.TryInsertAsync(chainId, sellerLower, req.PaymentReference, idempotencyKey,
            JsonSerializer.Serialize(req), ct);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Failed to insert fulfillment run record");
        return Results.Json(new { error = "Internal error", details = "Failed to create run record" }, statusCode: 500);
    }

    // Check if already fulfilled — first by idempotency key, then by payment reference
    // (covers both explicit idempotency keys and replays with new keys but same payment_reference)
    var existing = await runStore.GetByIdempotencyKeyAsync(idempotencyKey, ct);
    if (existing == null || existing.Id == runId)
    {
        // No match by idempotency key, or the insert returned the same ID (new run).
        // Check by payment reference in case a prior run with a different key exists.
        string? priorStatus = await runStore.GetStatusAsync(chainId, sellerLower, req.PaymentReference, ct);
        if (priorStatus == "completed")
        {
            // Look up the full record for the response
            await using var conn = new NpgsqlConnection(connString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT wc_order_id, wc_order_number FROM wc_fulfillment_runs
                WHERE chain_id = @c AND seller_address = @s AND payment_reference = @p AND status = 'completed'
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("@c", chainId);
            cmd.Parameters.AddWithValue("@s", sellerLower);
            cmd.Parameters.AddWithValue("@p", req.PaymentReference);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            int? wcOrderId = null;
            string? wcOrderNumber = null;
            if (await reader.ReadAsync(ct))
            {
                wcOrderId = reader.IsDBNull(0) ? null : reader.GetInt32(0);
                wcOrderNumber = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
            return Results.Json(new Dictionary<string, object?>
            {
                ["@type"] = "circles:WooCommerceFulfillmentResult",
                ["status"] = "ok",
                ["orderId"] = req.OrderId,
                ["paymentReference"] = req.PaymentReference,
                ["seller"] = sellerLower,
                ["message"] = "Already fulfilled",
                ["wcOrderId"] = wcOrderId,
                ["wcOrderNumber"] = wcOrderNumber
            });
        }
    }
    if (existing != null && existing.Status == "completed")
    {
        return Results.Json(new Dictionary<string, object?>
        {
            ["@type"] = "circles:WooCommerceFulfillmentResult",
            ["status"] = "ok",
            ["orderId"] = req.OrderId,
            ["paymentReference"] = req.PaymentReference,
            ["seller"] = sellerLower,
            ["message"] = "Already fulfilled",
            ["wcOrderId"] = existing.WcOrderId,
            ["wcOrderNumber"] = existing.WcOrderNumber
        });
    }

    // Resolve WooCommerce credentials
    var (wcClient, wcInfo, wcError) = await ResolveWooCommerceClientAsync(chainId, seller, connResolver, httpFactory, loggerFactory, ct);
    if (wcError != null)
    {
        await runStore.MarkErrorAsync(chainId, sellerLower, req.PaymentReference, "WooCommerce connection not found", ct);
        return wcError;
    }

    var inheritAbort = wcInfo!.InheritRequestAbort;

    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(wcInfo.TimeoutMs));
    using var linkedCts = inheritAbort
        ? CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, lifetime.ApplicationStopping, ct)
        : CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, lifetime.ApplicationStopping);
    var jobCt = linkedCts.Token;

    // Upsert WC customer
    string buyerAddress = req.Buyer?.Trim().ToLowerInvariant() ?? sellerLower;
    int customerId = wcInfo.DefaultCustomerId ?? 0;

    if (req.ContactPoint?.Email != null || req.ShippingAddress != null || req.BillingAddress != null)
    {
        try
        {
            var billing = MapAddress(req.BillingAddress ?? req.ShippingAddress);
            var shipping = MapAddress(req.ShippingAddress);
            customerId = await wcClient!.GetOrCreateCustomerAsync(
                req.ContactPoint?.Email ?? $"buyer-{buyerAddress}@localhost",
                req.Customer?.GivenName, req.Customer?.FamilyName, billing, shipping, jobCt);
            log.LogInformation("WC customer resolved/created: id={CustomerId}", customerId);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to create WC customer; using default");
            if (wcInfo.DefaultCustomerId.HasValue)
                customerId = wcInfo.DefaultCustomerId.Value;
        }
    }

    // Build order line items
    var lineItems = new List<WcLineItem>();
    var decrementedStock = new List<(string Sku, int Qty)>();

    foreach (var item in req.Items ?? Enumerable.Empty<WooCommerceFulfillmentItem>())
    {
        var mapping = await mapper.ResolveAsync(chainId, sellerLower, item.Sku, jobCt);
        if (mapping == null) continue;

        int qty = Math.Max(1, (int)Math.Floor(item.Quantity));

        // Check local stock
        var localAvailable = await stockStore.GetAvailableQtyAsync(chainId, sellerLower, item.Sku, jobCt);
        if (localAvailable.HasValue)
        {
            bool decremented = await stockStore.TryDecrementAsync(chainId, sellerLower, item.Sku, qty, jobCt);
            if (!decremented)
            {
                // Rollback
                foreach (var d in decrementedStock)
                    await stockStore.IncrementAsync(chainId, sellerLower, d.Sku, d.Qty, CancellationToken.None);

                await runStore.MarkErrorAsync(chainId, sellerLower, req.PaymentReference, $"Insufficient local stock for {item.Sku}", ct);
                return Results.Json(new Dictionary<string, object?>
                {
                    ["@type"] = "circles:WooCommerceFulfillmentResult",
                    ["status"] = "error",
                    ["orderId"] = req.OrderId,
                    ["paymentReference"] = req.PaymentReference,
                    ["seller"] = sellerLower,
                    ["sku"] = item.Sku,
                    ["message"] = $"Insufficient local stock for sku={item.Sku}",
                    ["codes"] = new[] { "insufficientStock" }
                }, statusCode: 409);
            }
            decrementedStock.Add((item.Sku, qty));
        }

        lineItems.Add(new WcLineItem
        {
            ProductId = mapping.WcProductId,
            Sku = mapping.WcProductSku,
            Quantity = qty
        });
    }

    if (lineItems.Count == 0)
    {
        await runStore.MarkOkAsync(chainId, sellerLower, req.PaymentReference, ct);
        return Results.Json(new Dictionary<string, object?>
        {
            ["@type"] = "circles:WooCommerceFulfillmentResult",
            ["status"] = "notApplicable",
            ["orderId"] = req.OrderId,
            ["paymentReference"] = req.PaymentReference,
            ["seller"] = sellerLower,
            ["message"] = "No items mapped to WooCommerce"
        });
    }

    // Create WC order
    try
    {
        var orderRequest = new WcCreateOrderRequest
        {
            CustomerId = customerId > 0 ? customerId : null,
            Status = wcInfo.OrderStatus,
            Billing = MapAddress(req.BillingAddress ?? req.ShippingAddress),
            Shipping = MapAddress(req.ShippingAddress),
            LineItems = lineItems,
            Currency = req.Currency ?? "EUR",
            MetaData = new List<WcMetaData>
            {
                new() { Key = "circles_order_id", Value = req.OrderId },
                new() { Key = "circles_payment_reference", Value = req.PaymentReference },
                new() { Key = "circles_chain_id", Value = chainId.ToString() }
            }
        };

        var order = await wcClient!.CreateOrderAsync(orderRequest, jobCt);

        await runStore.SetOrderInfoAsync(runId, order.Id, order.Number ?? order.Id.ToString(), ct);
        await runStore.MarkOkAsync(chainId, sellerLower, req.PaymentReference, ct);

        log.LogInformation("WooCommerce order created: {OrderId} (#{Number}) for {Seller}", order.Id, order.Number, sellerLower);

        return Results.Json(new Dictionary<string, object?>
        {
            ["@type"] = "circles:WooCommerceFulfillmentResult",
            ["status"] = "ok",
            ["orderId"] = req.OrderId,
            ["paymentReference"] = req.PaymentReference,
            ["seller"] = sellerLower,
            ["message"] = $"WooCommerce order #{order.Number ?? order.Id.ToString()} created",
            ["wcOrderId"] = order.Id,
            ["wcOrderNumber"] = order.Number,
            ["wcOrderStatus"] = order.Status
        });
    }
    catch (WooCommerceApiException ex) when (ex.IsValidationError)
    {
        foreach (var d in decrementedStock)
            await stockStore.IncrementAsync(chainId, sellerLower, d.Sku, d.Qty, CancellationToken.None);

        await runStore.MarkErrorAsync(chainId, sellerLower, req.PaymentReference, ex.Message, ct);
        return Results.Json(new Dictionary<string, object?>
        {
            ["@type"] = "circles:WooCommerceFulfillmentResult",
            ["status"] = "error",
            ["orderId"] = req.OrderId,
            ["paymentReference"] = req.PaymentReference,
            ["seller"] = sellerLower,
            ["message"] = $"WooCommerce validation error: {ex.Message}",
            ["codes"] = new[] { "validation_error" }
        }, statusCode: 422);
    }
    catch (Exception ex)
    {
        foreach (var d in decrementedStock)
        {
            try { await stockStore.IncrementAsync(chainId, sellerLower, d.Sku, d.Qty, CancellationToken.None); }
            catch (Exception rollbackEx)
            {
                log.LogError(rollbackEx, "Failed to rollback local stock for sku={Sku}", d.Sku);
            }
        }

        string msg = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
        await runStore.MarkErrorAsync(chainId, sellerLower, req.PaymentReference, msg, ct);

        log.LogError(ex, "Failed to create WooCommerce order for {Seller} orderId={OrderId}", sellerLower, req.OrderId);

        return Results.Json(new Dictionary<string, object?>
        {
            ["@type"] = "circles:WooCommerceFulfillmentResult",
            ["status"] = "error",
            ["orderId"] = req.OrderId,
            ["paymentReference"] = req.PaymentReference,
            ["seller"] = sellerLower,
            ["message"] = ex.Message,
            ["codes"] = new[] { "wc_api_error" }
        }, statusCode: 500);
    }
});

static WcAddressDto? MapAddress(WooCommerceFulfillmentAddress? a)
{
    if (a == null) return null;
    return new WcAddressDto
    {
        FirstName = a.GivenName,
        LastName = a.FamilyName,
        Address1 = a.StreetAddress,
        City = a.AddressLocality,
        Postcode = a.PostalCode,
        Country = a.AddressCountry,
        Email = a.Email,
        Phone = a.Telephone
    };
}

publicApp.MapMetrics();

// ── Admin App ─────────────────────────────────────────────────────────────────

var adminBuilder = WebApplication.CreateBuilder(args);
adminBuilder.Logging.ClearProviders();
adminBuilder.Logging.AddConsole();
adminBuilder.WebHost.UseUrls($"http://0.0.0.0:{AdminPortConfig.GetAdminPort("WOOCOMMERCE_ADMIN_PORT", 5690)}");
adminBuilder.Services.AddHttpClient();
adminBuilder.Services.AddSingleton<IWooCommerceConnectionResolver>(sp =>
    new PostgresWooCommerceConnectionResolver(connString, sp.GetRequiredService<ILogger<PostgresWooCommerceConnectionResolver>>()));
adminBuilder.Services.AddSingleton<IProductMappingResolver>(sp =>
    new PostgresProductMappingResolver(connString, sp.GetRequiredService<ILogger<PostgresProductMappingResolver>>()));
adminBuilder.Services.AddAuthServiceJwks();

var adminApp = adminBuilder.Build();
adminApp.UseAuthentication();
adminApp.UseAuthorization();
adminApp.MapWooCommerceAdminApi("/admin", connString);

// ── Run Both ──────────────────────────────────────────────────────────────────

var publicTask = publicApp.RunAsync();
var adminTask = adminApp.RunAsync();
await Task.WhenAll(publicTask, adminTask);