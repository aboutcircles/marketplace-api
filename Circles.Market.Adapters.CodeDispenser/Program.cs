using System.Text.RegularExpressions;
using Circles.Market.Adapters.CodeDispenser;
using Microsoft.Extensions.Options;
using Npgsql;
using Circles.Market.Shared;

var builder = WebApplication.CreateBuilder(args);

// Read configuration from environment only (no config section fallback)
var postgresConn = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION");
if (string.IsNullOrWhiteSpace(postgresConn))
{
    throw new InvalidOperationException("Missing required POSTGRES_CONNECTION environment variable.");
}

var poolsDir = Environment.GetEnvironmentVariable("CODE_POOLS_DIR");

// Mapping is now DB-driven; no config binding
builder.Services.AddMemoryCache();

builder.Services.AddSingleton<ICodeMappingResolver>(sp =>
{
    var cache = sp.GetService<Microsoft.Extensions.Caching.Memory.IMemoryCache>();
    return new PostgresCodeMappingResolver(postgresConn, cache);
});

builder.Services.AddSingleton<ICodeDispenserStore>(sp =>
{
    return new PostgresCodeDispenserStore(postgresConn, sp.GetRequiredService<ILogger<PostgresCodeDispenserStore>>());
});

builder.Services.AddSingleton<Circles.Market.Adapters.CodeDispenser.Auth.ITrustedCallerAuth>(sp =>
{
    return new Circles.Market.Adapters.CodeDispenser.Auth.PostgresTrustedCallerAuth(postgresConn, sp.GetRequiredService<ILogger<Circles.Market.Adapters.CodeDispenser.Auth.PostgresTrustedCallerAuth>>());
});

builder.Services.AddLogging(o => o.AddConsole());

var app = builder.Build();

// Listen on port from ASPNETCORE_URLS or fallback to PORT or default 5680
var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
if (string.IsNullOrWhiteSpace(urls))
{
    int port = 5680;
    var portStr = Environment.GetEnvironmentVariable("PORT");
    if (!string.IsNullOrWhiteSpace(portStr) && int.TryParse(portStr, out var p) && p > 0 && p <= 65535)
        port = p;
    app.Urls.Add($"http://0.0.0.0:{port}");
}

// Bootstrap schema and optional seeding (opt-in only via CODE_POOLS_DIR)
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Bootstrap");
    var store = scope.ServiceProvider.GetRequiredService<ICodeDispenserStore>();
    await store.EnsureSchemaAsync(CancellationToken.None);

    // Only seed if CODE_POOLS_DIR is explicitly set and non-empty
    if (!string.IsNullOrWhiteSpace(poolsDir))
    {
        try
        {
            await store.SeedFromDirAsync(poolsDir, CancellationToken.None);
            logger.LogInformation("Code pools seeded from {PoolsDir}", poolsDir);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to seed code pools from {PoolsDir}; aborting.", poolsDir);
            throw; // Fail fast when seeding is explicitly requested
        }
    }
}

// Health endpoint
app.MapGet("/health", () => Results.Json(new { ok = true }));

static bool IsValidSeller(string seller)
{
    return !string.IsNullOrWhiteSpace(seller) && Regex.IsMatch(seller.Trim().ToLowerInvariant(), "^0x[0-9a-f]{40}$");
}

// Inventory feed endpoint (QuantitativeValue)
app.MapGet("/inventory/{chainId:long}/{seller}/{sku}", async (
    long chainId,
    string seller,
    string sku,
    HttpRequest httpRequest,
    ICodeDispenserStore store,
    ICodeMappingResolver mapper,
    Circles.Market.Adapters.CodeDispenser.Auth.ITrustedCallerAuth auth,
    ILoggerFactory loggerFactory,
    CancellationToken ct) =>
{
    var log = loggerFactory.CreateLogger("Inventory");
    if (!IsValidSeller(seller))
    {
        return Results.BadRequest(new { error = "Invalid seller address" });
    }
    if (string.IsNullOrWhiteSpace(sku))
    {
        return Results.BadRequest(new { error = "Missing sku" });
    }

    // Auth: require X-Circles-Service-Key with 'inventory' scope
    string? apiKey = httpRequest.Headers["X-Circles-Service-Key"].FirstOrDefault();
    var authRes = await auth.AuthorizeAsync(apiKey, requiredScope: "inventory", chainId: chainId, seller: seller, ct: ct);
    if (!authRes.Allowed)
    {
        return Results.Unauthorized();
    }

    if (!mapper.TryResolve(chainId, seller, sku, out var entry) || entry is null)
    {
        return Results.NotFound(new { error = "No mapping for seller/sku", chainId, seller = seller.ToLowerInvariant(), sku });
    }

    try
    {
        var remaining = await store.GetRemainingAsync(entry.PoolId, ct);
        var payload = new Dictionary<string, object?>
        {
            ["@type"] = "QuantitativeValue",
            ["value"] = remaining,
            ["unitCode"] = "C62"
        };
        return Results.Json(payload);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Failed to fetch inventory for seller={Seller} sku={Sku}", seller, sku);
        return Results.Problem("Inventory lookup failed");
    }
}).WithName("Inventory");

// Fulfillment endpoint
app.MapPost("/fulfill/{chainId:long}/{seller}", async (
    long chainId,
    string seller,
    HttpRequest httpRequest,
    ICodeDispenserStore store,
    ICodeMappingResolver mapper,
    Circles.Market.Adapters.CodeDispenser.Auth.ITrustedCallerAuth auth,
    ILoggerFactory loggerFactory,
    CancellationToken ct) =>
{
    var log = loggerFactory.CreateLogger("Fulfill");
    if (!IsValidSeller(seller))
    {
        return Results.BadRequest(new { error = "Invalid seller address" });
    }

    FulfillmentRequest? req;
    try
    {
        var jsonOptions = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        req = await System.Text.Json.JsonSerializer.DeserializeAsync<FulfillmentRequest>(httpRequest.Body, jsonOptions, ct);
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

    // Auth: require X-Circles-Service-Key with 'fulfill' scope
    string? apiKey = httpRequest.Headers["X-Circles-Service-Key"].FirstOrDefault();
    var authRes = await auth.AuthorizeAsync(apiKey, requiredScope: "fulfill", chainId: chainId, seller: seller, ct: ct);
    if (!authRes.Allowed)
    {
        return Results.Unauthorized();
    }

    // Resolve mapping for items
    var mapped = new List<(string sku, CodeMappingEntry entry)>();
    foreach (var it in req.Items)
    {
        if (mapper.TryResolve(chainId, seller, it.Sku, out var e) && e != null)
        {
            mapped.Add((it.Sku, e));
        }
    }

    if (mapped.Count == 0)
    {
        return Results.Json(new Dictionary<string, object?>
        {
            ["@context"] = new object[]{"https://schema.org/", "https://aboutcircles.com/contexts/circles-market/"},
            ["@type"] = "circles:CodeDispenserResult",
            ["status"] = "notApplicable",
            ["orderId"] = req.OrderId,
            ["paymentReference"] = req.PaymentReference,
            ["codes"] = Array.Empty<string>()
        });
    }

    // If multiple distinct pools or SKUs matched → ambiguous. Allow multiple lines for the same SKU/pool (quantity).
    var distinctPools = mapped.Select(m => m.entry.PoolId).Distinct(StringComparer.Ordinal).ToList();
    var distinctSkus = mapped.Select(m => m.sku).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    if (distinctPools.Count != 1 || distinctSkus.Count != 1)
    {
        return Results.Json(new Dictionary<string, object?>
        {
            ["@context"] = new object[]{"https://schema.org/", "https://aboutcircles.com/contexts/circles-market/"},
            ["@type"] = "circles:CodeDispenserResult",
            ["status"] = "ambiguous",
            ["orderId"] = req.OrderId,
            ["paymentReference"] = req.PaymentReference,
            ["codes"] = Array.Empty<string>()
        });
    }

    var picked = mapped[0];
    var issuedAt = DateTimeOffset.UtcNow;
    try
    {
        // Determine desired quantity from matching items; floor to int and clamp to >= 1; cap at 100
        decimal totalQtyDec = req.Items.Where(i => string.Equals(i.Sku, picked.sku, StringComparison.OrdinalIgnoreCase))
            .Select(i => i.Quantity)
            .DefaultIfEmpty(1)
            .Aggregate(0m, (acc, x) => acc + x);
        int desiredQty = (int)Math.Floor(totalQtyDec);
        if (desiredQty < 1) desiredQty = 1;
        if (desiredQty > 100)
        {
            return Results.BadRequest(new { error = "Requested quantity exceeds limit (max 100)" });
        }

        var (status, codes) = await store.AssignManyAsync(chainId, seller.ToLowerInvariant(), req.PaymentReference, req.OrderId, picked.sku, picked.entry.PoolId, desiredQty, ct);
        if (status == AssignmentStatus.Depleted || codes.Count == 0)
        {
            return Results.Json(new Dictionary<string, object?>
            {
                ["@context"] = new object[]{"https://schema.org/", "https://aboutcircles.com/contexts/circles-market/"},
                ["@type"] = "circles:CodeDispenserResult",
                ["status"] = "depleted",
                ["orderId"] = req.OrderId,
                ["paymentReference"] = req.PaymentReference,
                ["seller"] = seller.ToLowerInvariant(),
                ["sku"] = picked.sku,
                ["requestedQuantity"] = desiredQty,
                ["codes"] = Array.Empty<string>(),
                ["issuedAt"] = issuedAt.UtcDateTime.ToString("O")
            });
        }
        if (status == AssignmentStatus.Ok)
        {
            string? downloadUrl = null;
            string? firstCode = codes.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(picked.entry.DownloadUrlTemplate) && !string.IsNullOrEmpty(firstCode))
            {
                downloadUrl = picked.entry.DownloadUrlTemplate!.Replace("{code}", firstCode);
            }
            return Results.Json(new Dictionary<string, object?>
            {
                ["@context"] = new object[]{"https://schema.org/", "https://aboutcircles.com/contexts/circles-market/"},
                ["@type"] = "circles:CodeDispenserResult",
                ["status"] = "ok",
                ["orderId"] = req.OrderId,
                ["paymentReference"] = req.PaymentReference,
                ["seller"] = seller.ToLowerInvariant(),
                ["sku"] = picked.sku,
                ["codes"] = codes,
                ["downloadUrl"] = downloadUrl,
                ["issuedAt"] = issuedAt.UtcDateTime.ToString("O")
            });
        }

        // Fallback error
        return Results.Json(new Dictionary<string, object?>
        {
            ["@context"] = new object[]{"https://schema.org/", "https://aboutcircles.com/contexts/circles-market/"},
            ["@type"] = "circles:CodeDispenserResult",
            ["status"] = "error",
            ["orderId"] = req.OrderId,
            ["paymentReference"] = req.PaymentReference
        });
    }
    catch (PostgresException pex) when (pex.SqlState == "23505")
    {
        // Unique violation from a concurrent race. Re-run assignment to read existing idempotent rows and return OK if possible.
        try
        {
            // Recompute desired quantity for retry scope
            decimal totalQtyDec_retry = req!.Items.Where(i => string.Equals(i.Sku, picked.sku, StringComparison.OrdinalIgnoreCase))
                .Select(i => i.Quantity <= 0 ? 1 : i.Quantity)
                .DefaultIfEmpty(1)
                .Aggregate(0m, (acc, x) => acc + x);
            int desiredQty2 = (int)Math.Max(1, Math.Floor(totalQtyDec_retry));

            var retry = await store.AssignManyAsync(chainId, seller.ToLowerInvariant(), req.PaymentReference, req.OrderId, picked.sku, picked.entry.PoolId, desiredQty2, ct);
            if (retry.status == AssignmentStatus.Ok && retry.codes.Count > 0)
            {
                string? downloadUrl = null;
                string? firstCode = retry.codes.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(picked.entry.DownloadUrlTemplate) && !string.IsNullOrEmpty(firstCode))
                {
                    downloadUrl = picked.entry.DownloadUrlTemplate!.Replace("{code}", firstCode);
                }
                return Results.Json(new Dictionary<string, object?>
                {
                    ["@context"] = new object[]{"https://schema.org/", "https://aboutcircles.com/contexts/circles-market/"},
                    ["@type"] = "circles:CodeDispenserResult",
                    ["status"] = "ok",
                    ["orderId"] = req.OrderId,
                    ["paymentReference"] = req.PaymentReference,
                    ["seller"] = seller.ToLowerInvariant(),
                    ["sku"] = picked.sku,
                    ["codes"] = retry.codes,
                    ["downloadUrl"] = downloadUrl,
                    ["issuedAt"] = issuedAt.UtcDateTime.ToString("O")
                });
            }
        }
        catch { /* fall through to error payload below */ }

        return Results.Json(new Dictionary<string, object?>
        {
            ["@context"] = new object[]{"https://schema.org/", "https://aboutcircles.com/contexts/circles-market/"},
            ["@type"] = "circles:CodeDispenserResult",
            ["status"] = "error",
            ["orderId"] = req!.OrderId,
            ["paymentReference"] = req.PaymentReference,
            ["message"] = "unique constraint violation"
        });
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Fulfillment error");
        return Results.Json(new Dictionary<string, object?>
        {
            ["@context"] = new object[]{"https://schema.org/", "https://aboutcircles.com/contexts/circles-market/"},
            ["@type"] = "circles:CodeDispenserResult",
            ["status"] = "error",
            ["orderId"] = req.OrderId,
            ["paymentReference"] = req.PaymentReference
        });
    }
});

await app.RunAsync();
