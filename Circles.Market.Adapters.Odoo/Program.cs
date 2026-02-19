using System.Text.RegularExpressions;
using Circles.Market.Adapters.Odoo;
using Circles.Market.Adapters.Odoo.Admin;
using Circles.Market.Adapters.Odoo.Auth;
using Circles.Market.Adapters.Odoo.Db;
using Circles.Market.Auth.Siwe;
using Circles.Market.Fulfillment.Core;
using Circles.Market.Shared;
using Circles.Market.Shared.Admin;
using Prometheus;

var publicBuilder = WebApplication.CreateBuilder(args);

// Read DB connection from environment only
string? connString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION");
if (string.IsNullOrWhiteSpace(connString))
{
    throw new InvalidOperationException("Missing required POSTGRES_CONNECTION environment variable.");
}

// All configuration is now DB-driven via odoo_connections, inventory_mappings tables

publicBuilder.Services.AddSingleton<OdooDbBootstrapper>(sp =>
    new OdooDbBootstrapper(connString, sp.GetRequiredService<ILogger<OdooDbBootstrapper>>()));

// DB-backed components
publicBuilder.Services.AddSingleton<ITrustedCallerAuth>(sp =>
    new EnvTrustedCallerAuth(sp.GetRequiredService<ILogger<EnvTrustedCallerAuth>>()));
publicBuilder.Services.AddSingleton<IInventoryMappingResolver>(sp => new PostgresInventoryMappingResolver(connString, sp.GetRequiredService<ILogger<PostgresInventoryMappingResolver>>()));
publicBuilder.Services.AddSingleton<IOdooConnectionResolver>(sp => new PostgresOdooConnectionResolver(connString, sp.GetRequiredService<ILogger<PostgresOdooConnectionResolver>>()));

publicBuilder.Services.AddSingleton<IOdooFulfillmentRunStore>(sp =>
    new PostgresFulfillmentRunStore(
        connString,
        sp.GetRequiredService<ILogger<PostgresFulfillmentRunStore>>()));
publicBuilder.Services.AddSingleton<IFulfillmentRunStore>(sp => sp.GetRequiredService<IOdooFulfillmentRunStore>());

// Typed HttpClient for Odoo JSON-RPC.
publicBuilder.Services.AddHttpClient<OdooClient>();

publicBuilder.Services.AddMemoryCache();

var publicApp = publicBuilder.Build();

// Run schema bootstrap
using (var scope = publicApp.Services.CreateScope())
{
    var bootstrapper = scope.ServiceProvider.GetRequiredService<OdooDbBootstrapper>();
    await bootstrapper.EnsureSchemaAsync();
}

// Helper to authenticate
async Task<IResult?> Authorize(HttpContext context, ITrustedCallerAuth auth, string scope, long chainId, string seller, ILogger logger)
{
    string? apiKey = context.Request.Headers["X-Circles-Service-Key"];
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        logger.LogWarning("Authorize: Missing X-Circles-Service-Key header for scope={Scope} seller={Seller} chain={Chain}", scope, seller, chainId);
    }
    var result = await auth.AuthorizeAsync(apiKey, scope, chainId, seller, context.RequestAborted);
    if (!result.Allowed)
    {
        logger.LogWarning("Authorize: Denied access for scope={Scope} seller={Seller} chain={Chain}. Reason: {Reason}", scope, seller, chainId, result.Reason);
        return Results.Json(new { error = "Unauthorized", reason = result.Reason }, statusCode: 401);
    }
    return null;
}

// Helper to resolve OdooClient
async Task<(IResult? error, OdooClient? client)> ResolveOdoo(long chainId, string seller, IOdooConnectionResolver resolver, IHttpClientFactory httpFactory, ILoggerFactory loggerFactory, CancellationToken ct)
{
    var settings = await resolver.ResolveAsync(chainId, seller, ct);
    if (settings == null)
    {
        return (Results.Json(new { error = "No Odoo connection configured for this seller/chain." }, statusCode: 404), null);
    }
    var http = httpFactory.CreateClient();
    var client = new OdooClient(http, settings, loggerFactory.CreateLogger<OdooClient>());
    await client.UpdateBaseAddressAsync(ct);
    return (null, client);
}

// Listen on configurable port: use ASPNETCORE_URLS if provided, otherwise
// fall back to PORT env or default to 5678. This allows docker-compose to
// override the listening port easily while keeping a sensible default.
var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
bool hasUrls = !string.IsNullOrWhiteSpace(urls);
if (!hasUrls)
{
    string? portStr = Environment.GetEnvironmentVariable("PORT");
    int port;
    if (!int.TryParse(portStr, out port) || port <= 0 || port > 65535)
    {
        port = 5678;
    }
    publicApp.Urls.Add($"http://0.0.0.0:{port}");
}

publicApp.UseHttpMetrics();

// Health endpoint for orchestration
publicApp.MapGet("/health", () => Results.Json(new { ok = true }));

// ---------------------------------------------------------------------
// GET /inventory
// Returns a QuantitativeValue JSON object with current stock for the
// mapped Odoo product.
// Response shape matches product-catalog §4.2 inventoryFeed.
// ---------------------------------------------------------------------
publicApp.MapGet("/inventory/{chainId}/{seller}/{sku}", async (
        HttpContext context,
        long chainId,
        string seller,
        string sku,
        ITrustedCallerAuth auth,
        IOdooConnectionResolver odooResolver,
        IInventoryMappingResolver mapper,
        IHttpClientFactory httpFactory,
        ILoggerFactory loggerFactory,
        CancellationToken ct) =>
    {
        var authError = await Authorize(context, auth, "inventory", chainId, seller, loggerFactory.CreateLogger("Auth"));
        if (authError != null) return authError;

        var (mapped, mapping) = await mapper.TryResolveAsync(chainId, seller, sku, ct);
        if (!mapped || mapping is null)
        {
            return Results.NotFound(new
            {
                error = "No inventory mapping found for the requested operator/seller/sku.",
                sellerAddress = seller,
                sku
            });
        }

        var (odooError, odoo) = await ResolveOdoo(chainId, seller, odooResolver, httpFactory, loggerFactory, ct);
        if (odooError != null) return odooError;

        OdooProductTemplateStockDto? stock;
        try
        {
            stock = await odoo!.GetProductStockByCodeAsync(mapping.OdooProductCode, ct);
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("Inventory").LogError(ex, "Failed to get stock from Odoo for {Seller} {Sku}", seller, sku);
            return Results.Json(new { error = "Upstream auth failed or other error", details = ex.Message }, statusCode: 502);
        }

        bool stockMissing = stock is null;
        if (stockMissing)
        {
            return Results.NotFound(new
            {
                error = "Mapped Odoo product not found.",
                odooProductCode = mapping.OdooProductCode
            });
        }

        long value = (long)decimal.Truncate(stock!.QtyAvailable);
        bool negative = value < 0;
        if (negative)
        {
            value = 0;
        }

        var payload = new Dictionary<string, object?>
        {
            ["@type"] = "QuantitativeValue",
            ["value"] = value,
            ["unitCode"] = "C62"
        };

        return Results.Json(payload, (System.Text.Json.JsonSerializerOptions?)Circles.Profiles.Models.JsonSerializerOptions.JsonLd);
    });

// ---------------------------------------------------------------------
// GET /availability
// Returns an ItemAvailability JSON object (schema:InStock or schema:OutOfStock).
// ---------------------------------------------------------------------
publicApp.MapGet("/availability/{chainId}/{seller}/{sku}", async (
        HttpContext context,
        long chainId,
        string seller,
        string sku,
        ITrustedCallerAuth auth,
        IOdooConnectionResolver odooResolver,
        IInventoryMappingResolver mapper,
        IHttpClientFactory httpFactory,
        ILoggerFactory loggerFactory,
        CancellationToken ct) =>
    {
        var authError = await Authorize(context, auth, "inventory", chainId, seller, loggerFactory.CreateLogger("Auth"));
        if (authError != null) return authError;

        var (mapped, mapping) = await mapper.TryResolveAsync(chainId, seller, sku, ct);
        if (!mapped || mapping is null)
        {
            return Results.NotFound(new
            {
                error = "No inventory mapping found for the requested operator/seller/sku.",
                sellerAddress = seller,
                sku
            });
        }

        var (odooError, odoo) = await ResolveOdoo(chainId, seller, odooResolver, httpFactory, loggerFactory, ct);
        if (odooError != null) return odooError;

        OdooProductTemplateStockDto? stock;
        try
        {
            stock = await odoo!.GetProductStockByCodeAsync(mapping.OdooProductCode, ct);
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("Availability").LogError(ex, "Failed to get stock from Odoo for {Seller} {Sku}", seller, sku);
            return Results.Json(new { error = "Upstream auth failed or other error", details = ex.Message }, statusCode: 502);
        }

        bool stockMissing = stock is null;
        if (stockMissing)
        {
            return Results.NotFound(new
            {
                error = "Mapped Odoo product not found.",
                odooProductCode = mapping.OdooProductCode
            });
        }

        bool hasStock = stock!.QtyAvailable > 0;
        string availability = hasStock ? "https://schema.org/InStock" : "https://schema.org/OutOfStock";

        return Results.Json(
            availability,
            (System.Text.Json.JsonSerializerOptions?)Circles.Profiles.Models.JsonSerializerOptions.JsonLd
        );
    });

static bool IsValidSeller(string seller)
{
    return !string.IsNullOrWhiteSpace(seller) && Regex.IsMatch(seller.Trim().ToLowerInvariant(), "^0x[0-9a-f]{40}$");
}

// ---------------------------------------------------------------------
// POST /fulfill
// Creates and confirms an Odoo sale.order via JSON-RPC for mapped products.
// ---------------------------------------------------------------------
publicApp.MapPost("/fulfill/{chainId:long}/{seller}", async (
        long chainId,
        string seller,
        HttpContext context,
        ITrustedCallerAuth auth,
        IOdooConnectionResolver odooResolver,
        IInventoryMappingResolver mapper,
        IOdooFulfillmentRunStore runStore,
        IHostApplicationLifetime lifetime,
        IHttpClientFactory httpFactory,
        ILoggerFactory loggerFactory,
        CancellationToken ct) =>
    {
        var log = loggerFactory.CreateLogger("Fulfill");

        static Dictionary<string, object?> JsonLdResult(string status, string orderId, string paymentReference, string? seller, string? sku, string? message, string[] codes)
        {
            return new Dictionary<string, object?>
            {
                ["@context"] = new object[] { "https://schema.org/", "https://aboutcircles.com/contexts/circles-market/" },
                ["@type"] = "circles:OdooFulfillmentResult",
                ["status"] = status,
                ["orderId"] = orderId,
                ["paymentReference"] = paymentReference,
                ["seller"] = seller,
                ["sku"] = sku,
                ["message"] = message,
                ["codes"] = codes
            };
        }

        if (!IsValidSeller(seller))
        {
            return Results.BadRequest(new { error = "Invalid seller address" });
        }

        var authError = await Authorize(context, auth, "fulfill", chainId, seller, loggerFactory.CreateLogger("Auth"));
        if (authError != null) return authError;

        // Parse request
        FulfillmentRequest? req;
        try
        {
            var jsonOptions = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            req = await System.Text.Json.JsonSerializer.DeserializeAsync<FulfillmentRequest>(context.Request.Body, jsonOptions, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to parse request body");
            return Results.BadRequest(new { error = "Invalid JSON body" });
        }

        string? err = null;
        if (req is null || !req.TryNormalizeAndValidate(out err))
        {
            return Results.BadRequest(new { error = err ?? "Invalid request" });
        }

        string sellerLower = seller.ToLowerInvariant();

        // Idempotency check: fulfillment_runs
        var gate = await FulfillmentRunGate.TryAcquireAsync(runStore, chainId, sellerLower, req.PaymentReference, req.OrderId, ct);
        if (gate.State != FulfillmentRunGateState.Acquired)
        {
            if (gate.State == FulfillmentRunGateState.AlreadyProcessed)
            {
                return Results.Json(
                    JsonLdResult("ok", req.OrderId, req.PaymentReference, sellerLower, null, "Already processed", Array.Empty<string>()),
                    (System.Text.Json.JsonSerializerOptions?)Circles.Profiles.Models.JsonSerializerOptions.JsonLd);
            }

            if (gate.State == FulfillmentRunGateState.InProgress)
            {
                return Results.Json(
                    JsonLdResult("ok", req.OrderId, req.PaymentReference, sellerLower, null, "Already in progress", Array.Empty<string>()),
                    (System.Text.Json.JsonSerializerOptions?)Circles.Profiles.Models.JsonSerializerOptions.JsonLd);
            }

            // Fail closed: do not proceed if lock not acquired
            return Results.Json(
                JsonLdResult("error", req.OrderId, req.PaymentReference, sellerLower, null, "Could not acquire fulfillment lock", Array.Empty<string>()),
                (System.Text.Json.JsonSerializerOptions?)Circles.Profiles.Models.JsonSerializerOptions.JsonLd,
                statusCode: 500);
        }

        // --- AFTER LOCK ACQUISITION: avoid using request 'ct' for operations that MUST complete ---
        // We use a separate CTS for Odoo/Mapping resolution that isn't tied to the request abort yet,
        // because we want to make sure we can MarkErrorAsync if resolution fails.
        // We'll use a 30s timeout for this setup phase.
        using var setupCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
        setupCts.CancelAfter(TimeSpan.FromSeconds(30));
        var setupCt = setupCts.Token;

        var (odooError, odoo) = await ResolveOdoo(chainId, seller, odooResolver, httpFactory, loggerFactory, setupCt);
        if (odooError != null)
        {
            await runStore.MarkErrorAsync(chainId, sellerLower, req.PaymentReference, "Odoo resolution failed", setupCt);
            return odooError;
        }

        var timeoutMs = odoo!.GetSettings().JsonRpcTimeoutMs;
        var inheritAbort = odoo.GetSettings().FulfillInheritRequestAbort;

        log.LogInformation("Fulfillment start for {Seller} {Chain}: timeout={Timeout}ms, inheritAbort={InheritAbort}",
            sellerLower, chainId, timeoutMs, inheritAbort);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));

        // jobCt is NOT request-scoped (no 'ct'), it only stops if timeout or app stop
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, lifetime.ApplicationStopping);
        var jobCt = jobCts.Token;

        using var linked = inheritAbort
            ? CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, lifetime.ApplicationStopping, ct)
            : CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, lifetime.ApplicationStopping);

        var odooCt = linked.Token;

        var partnerId = odoo!.GetSettings().SalePartnerId;
        if (!partnerId.HasValue || partnerId.Value <= 0)
        {
            const string msg = "sale_partner_id not configured in odoo_connections for this seller/chain";
            log.LogError(msg);
            await runStore.MarkErrorAsync(chainId, sellerLower, req.PaymentReference, msg, jobCt);

            return Results.Json(
                JsonLdResult("error", req.OrderId, req.PaymentReference, sellerLower, null, msg, Array.Empty<string>()),
                (System.Text.Json.JsonSerializerOptions?)Circles.Profiles.Models.JsonSerializerOptions.JsonLd,
                statusCode: 500);
        }

        // Build sale order lines: SKU -> mapping default_code -> product.product id
        var lines = new List<SaleOrderLineDto>();

        foreach (var item in req.Items)
        {
            var (mapped, mapping) = await mapper.TryResolveAsync(chainId, seller, item.Sku, jobCt);
            if (!mapped || mapping is null) continue;

            int qty = (int)Math.Floor(item.Quantity);
            if (qty < 1) qty = 1;

            int productVariantId = await odoo.ResolveProductVariantIdByCodeAsync(mapping.OdooProductCode, odooCt);

            lines.Add(new SaleOrderLineDto
            {
                ProductId = productVariantId,
                Quantity = qty,
                UnitPrice = null // let Odoo compute price; colleague test also omits price_unit
            });
        }

        if (lines.Count == 0)
        {
            await runStore.MarkOkAsync(chainId, sellerLower, req.PaymentReference, jobCt);
            return Results.Json(
                JsonLdResult("notApplicable", req.OrderId, req.PaymentReference, sellerLower, null, "No items mapped to Odoo", Array.Empty<string>()),
                (System.Text.Json.JsonSerializerOptions?)Circles.Profiles.Models.JsonSerializerOptions.JsonLd);
        }

        try
        {
            var create = new SaleOrderCreateDto
            {
                PartnerId = partnerId.Value,
                Lines = lines
            };

            int odooOrderId = await odoo.CreateSaleOrderAsync(create, odooCt);
            var orderRead = await odoo.ReadSaleOrderAsync(odooOrderId, odooCt);
            string orderName = orderRead.Name ?? $"sale.order:{odooOrderId}";

            await odoo.ConfirmSaleOrderAsync(odooOrderId, odooCt);

            // Tracking lookup (may be missing early; that's OK)
            var picking = await odoo.GetOperationDetailsByOriginAsync(orderName, odooCt);
            string? tracking = picking?.CarrierTrackingRef;

            await runStore.SetOdooOrderInfoAsync(chainId, sellerLower, req.PaymentReference, odooOrderId, orderName, jobCt);
            await runStore.MarkOkAsync(chainId, sellerLower, req.PaymentReference, jobCt);

            string msg = tracking != null
                ? $"Odoo order {orderName} confirmed. Tracking: {tracking}"
                : $"Odoo order {orderName} confirmed.";

            return Results.Json(
                JsonLdResult("ok", req.OrderId, req.PaymentReference, sellerLower, null, msg, Array.Empty<string>()),
                (System.Text.Json.JsonSerializerOptions?)Circles.Profiles.Models.JsonSerializerOptions.JsonLd);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to create/confirm sale order for order {OrderId}", req.OrderId);

            string msg = ex.Message;
            if (msg.Length > 2000) msg = msg.Substring(0, 2000);

            await runStore.MarkErrorAsync(chainId, sellerLower, req.PaymentReference, msg, jobCt);

            return Results.Json(
                JsonLdResult("error", req.OrderId, req.PaymentReference, sellerLower, null, msg, Array.Empty<string>()),
                (System.Text.Json.JsonSerializerOptions?)Circles.Profiles.Models.JsonSerializerOptions.JsonLd,
                statusCode: 500);
        }
    });

publicApp.MapMetrics();

var adminBuilder = WebApplication.CreateBuilder(args);
adminBuilder.Logging.ClearProviders();
adminBuilder.Logging.AddConsole();
adminBuilder.WebHost.UseUrls($"http://0.0.0.0:{AdminPortConfig.GetAdminPort("ODOO_ADMIN_PORT", 5688)}");
adminBuilder.Services.AddHttpClient();
adminBuilder.Services.AddSingleton<IOdooConnectionResolver>(sp =>
    new PostgresOdooConnectionResolver(connString, sp.GetRequiredService<ILogger<PostgresOdooConnectionResolver>>()));
adminBuilder.Services.AddAdminJwtValidation(new SiweAuthOptions
{
    JwtSecretEnv = "ADMIN_JWT_SECRET",
    JwtIssuerEnv = "ADMIN_JWT_ISSUER",
    JwtAudienceEnv = "ADMIN_JWT_AUDIENCE"
}, AdminAuthConstants.Scheme);

var adminApp = adminBuilder.Build();
adminApp.UseAuthentication();
adminApp.UseAuthorization();
adminApp.MapOdooAdminApi("/admin", connString);

var publicTask = publicApp.RunAsync();
var adminTask = adminApp.RunAsync();
await Task.WhenAll(publicTask, adminTask);
