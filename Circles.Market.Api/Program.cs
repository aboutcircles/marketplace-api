using System.Threading.RateLimiting;
using Circles.Market.Api;
using Circles.Market.Api.Cart;
using Circles.Market.Api.Catalog;
using Circles.Profiles.Interfaces;
using Circles.Profiles.Sdk;
using Nethereum.Web3;
using Circles.Profiles.Market;
using Microsoft.AspNetCore.Mvc;
using Circles.Market.Api.Inventory;
using Circles.Market.Api.Payments;
using Circles.Market.Api.Auth;
using Circles.Market.Api.Cart.Validation;
using Circles.Market.Api.Fulfillment;
using Circles.Market.Api.Routing;
using Circles.Market.Api.Pin;
using System.Data.Common;

var builder = WebApplication.CreateBuilder(args);

// Observability & config
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

// Memory cache with a global size cap (200 MiB)
builder.Services.AddMemoryCache(o => o.SizeLimit = 200 * 1024 * 1024);

// Rate limiting: simple fixed-window per-IP
builder.Services.AddRateLimiter(o =>
{
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    {
        string key = ctx.Connection.RemoteIpAddress?.ToString() ?? "anon";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 60,
            Window = TimeSpan.FromMinutes(1),
            AutoReplenishment = true,
            QueueLimit = 0
        });
    });
});

var chainRpcUrl = Environment.GetEnvironmentVariable("RPC")
                  ?? throw new Exception("The RPC env variable is not set.");
var ipfsRpcUrl = Environment.GetEnvironmentVariable("IPFS_RPC_URL") ??
                 throw new Exception("The IPFS_RPC_URL env variable is not set.");
var ipfsRpcBearer = Environment.GetEnvironmentVariable("IPFS_RPC_BEARER") ??
                    throw new Exception("The IPFS_RPC_BEARER env variable is not set.");
var ipfsGatewayUrl = Environment.GetEnvironmentVariable("IPFS_GATEWAY_URL") ??
                     throw new Exception("The IPFS_GATEWAY_URL env variable is not set.");

// IPFS store: inner RPC client + CID-keyed caching proxy
builder.Services.AddSingleton<IIpfsStore>(_ => new IpfsRpcApiStore(ipfsRpcUrl, ipfsRpcBearer, ipfsGatewayUrl));
builder.Services.Decorate<IIpfsStore, CachingIpfsStore>();

// Chain + registry
builder.Services.AddSingleton<INameRegistry>(_ => new NameRegistry(chainRpcUrl));
builder.Services.AddSingleton<IChainApi>(_ =>
    new EthereumChainApi(new Web3(chainRpcUrl), Helpers.DefaultChainId));

// Signature verification:
// Register one DefaultSignatureVerifier instance with logging and expose it as both interfaces.
builder.Services.AddSingleton<DefaultSignatureVerifier>(sp =>
    new DefaultSignatureVerifier(
        sp.GetRequiredService<IChainApi>(),
        sp.GetRequiredService<ILogger<DefaultSignatureVerifier>>()));
builder.Services.AddSingleton<ISignatureVerifier>(sp =>
    sp.GetRequiredService<DefaultSignatureVerifier>());
builder.Services.AddSingleton<ISafeBytesVerifier>(sp =>
    sp.GetRequiredService<DefaultSignatureVerifier>());

// Cart & Order services: require Postgres
// Expect a standard Npgsql (.NET) connection string via POSTGRES_CONNECTION
string? pgConn = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION")
                 ?? throw new Exception("POSTGRES_CONNECTION env variable is required to run the service.");

if (string.IsNullOrWhiteSpace(pgConn))
    throw new Exception("POSTGRES_CONNECTION env variable is required to run the service.");

bool autoMigrate = string.Equals(Environment.GetEnvironmentVariable("DB_AUTO_MIGRATE"), "true", StringComparison.OrdinalIgnoreCase);

builder.Services.AddSingleton<IBasketStore>(sp =>
    new PostgresBasketStore(pgConn!, sp.GetRequiredService<ILogger<PostgresBasketStore>>()));

builder.Services.AddSingleton<IOrderStore>(sp =>
    new PostgresOrderStore(pgConn!, sp.GetRequiredService<ILogger<PostgresOrderStore>>()));

// Order access service (buyer-scoped reads + ownership checks)
builder.Services.AddSingleton<IOrderAccessService, OrderAccessService>();

// Payments store (schema bootstrap on construction)
builder.Services.AddSingleton<IPaymentStore>(sp =>
    new PostgresPaymentStore(pgConn!, sp.GetRequiredService<ILogger<PostgresPaymentStore>>()));

// Order payment flow: single owner of payment→order lifecycle
// SSE bus + hooks that publish status changes to subscribers
builder.Services.AddSingleton<IOrderStatusEventBus, InMemoryOrderStatusEventBus>();
builder.Services.AddSingleton<IOrderLifecycleHooks>(sp =>
    new SseOrderLifecycleHooks(
        sp.GetRequiredService<IOrderStatusEventBus>(),
        sp.GetRequiredService<IOrderStore>(),
        sp.GetRequiredService<IOrderFulfillmentClient>(),
        sp.GetRequiredService<IMarketRouteStore>(),
        sp.GetRequiredService<ILogger<SseOrderLifecycleHooks>>()));
builder.Services.AddSingleton<IOrderPaymentFlow, OrderPaymentFlow>();

// Fulfillment/Inventory HTTP clients: use named clients with no default auth headers
builder.Services.AddHttpClient();

builder.Services.AddHttpClient("fulfillment_public", client =>
{
    client.DefaultRequestHeaders.Clear();
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

builder.Services.AddHttpClient("fulfillment_trusted", client =>
{
    client.DefaultRequestHeaders.Clear();
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

builder.Services.AddHttpClient("inventory_public", client =>
{
    client.DefaultRequestHeaders.Clear();
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

builder.Services.AddHttpClient("inventory_trusted", client =>
{
    client.DefaultRequestHeaders.Clear();
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });


builder.Services.AddSingleton<IOutboundServiceAuthProvider>(sp =>
    new EnvOutboundServiceAuthProvider(sp.GetRequiredService<ILogger<EnvOutboundServiceAuthProvider>>()));

builder.Services.AddSingleton<IMarketRouteStore>(sp =>
    new PostgresMarketRouteStore(
        pgConn!,
        sp.GetRequiredService<ILogger<PostgresMarketRouteStore>>(),
        sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>()));

builder.Services.AddSingleton<IOrderFulfillmentClient, HttpOrderFulfillmentClient>();

// Payments poller: polls CrcV2.PaymentReceived via circles_query and persists to Postgres
builder.Services.AddHostedService<CirclesPaymentsPoller>();

// Cart validation rules (now offer-driven: keep ItemsNonEmpty + OfferRequiredSlots only)
builder.Services.AddSingleton<ICartRule, ItemsNonEmptyRule>();
builder.Services.AddSingleton<ICartRule, OfferRequiredSlotsRule>();
builder.Services.AddSingleton<ICartRule, CustomerNameRule>();

builder.Services.AddSingleton<ICartValidator, CartValidator>();
builder.Services.AddSingleton<IProductResolver, ProductResolver>();
builder.Services.AddSingleton<IBasketCanonicalizer, BasketCanonicalizer>();


// Live inventory client for enforcing inventory limits in basket canonicalization
builder.Services.AddSingleton<ILiveInventoryClient, LiveInventoryClient>();

// One-off sales store for sold-once default availability
builder.Services.AddSingleton<IOneOffSalesStore>(sp =>
    new PostgresOneOffSalesStore(pgConn!, sp.GetRequiredService<ILogger<PostgresOneOffSalesStore>>()));

// JSON-LD shape verification for pin endpoint (allow only user-generated shapes)
builder.Services.AddSingleton<IJsonLdShapeVerifier, JsonLdShapeVerifier>();

// Aggregator service
builder.Services.AddSingleton(sp =>
    new Circles.Profiles.Aggregation.BasicAggregator(
        new TimeoutIpfsStore(
            sp.GetRequiredService<IIpfsStore>(),
            TimeSpan.FromMilliseconds(
                int.TryParse(Environment.GetEnvironmentVariable("CATALOG_AVATAR_PROFILE_TIMEOUT_MS"), out var ms) && ms > 0
                    ? ms
                    : 1000)),
        sp.GetRequiredService<INameRegistry>(),
        sp.GetRequiredService<ISignatureVerifier>()));
builder.Services.AddSingleton<CatalogReducer>();
builder.Services.AddSingleton<OperatorCatalogService>();

// OpenAPI
builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.OpenApiInfo
    {
        Title = "Circles Market API",
        Version = "v1",
        Description = "Swagger UI for Circles.Market.Api"
    });
});

// HttpClient for dereferencing availability/inventory feeds
builder.Services.AddHttpClient();

// CORS: allow all origins/headers/methods (for demo/tooling) with exposed pagination headers
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
        policy
            .AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader()
            .WithExposedHeaders("X-Next-Cursor", "Link")
    );
});

// Auth: JWT + challenge store
builder.Services.AddJwtAuth();

var app = builder.Build();

static string SafeUrl(string url)
{
    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        return "[invalid url]";

    // Avoid leaking userinfo/query/fragment (often used for tokens)
    var hostPort = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
    return $"{uri.Scheme}://{hostPort}";
}

static string SafeToken(string? token)
{
    if (string.IsNullOrEmpty(token))
        return "[empty]";
    return $"[redacted,len={token.Length}]";
}

static string SafeConnectionString(string conn)
{
    try
    {
        var b = new DbConnectionStringBuilder { ConnectionString = conn };
        foreach (var k in new[] { "Password", "Pwd", "PWD", "password", "pwd" })
        {
            if (b.ContainsKey(k))
                b[k] = "[redacted]";
        }
        return b.ConnectionString;
    }
    catch
    {
        return $"[redacted,len={conn.Length}]";
    }
}

// Log startup settings (redacted where needed)
app.Logger.LogInformation("[startup-config] {Key}={Value}", "RPC", SafeUrl(chainRpcUrl));
app.Logger.LogInformation("[startup-config] {Key}={Value}", "IPFS_RPC_URL", SafeUrl(ipfsRpcUrl));
app.Logger.LogInformation("[startup-config] {Key}={Value}", "IPFS_RPC_BEARER", SafeToken(ipfsRpcBearer));
app.Logger.LogInformation("[startup-config] {Key}={Value}", "IPFS_GATEWAY_URL", SafeUrl(ipfsGatewayUrl));
app.Logger.LogInformation("[startup-config] {Key}={Value}", "POSTGRES_CONNECTION", SafeConnectionString(pgConn!));
app.Logger.LogInformation("[startup-config] {Key}={Value}", "DB_AUTO_MIGRATE", autoMigrate);
app.Logger.LogInformation("[startup-config] {Key}={Value}", "CATALOG_AVATAR_PROFILE_TIMEOUT_MS",
    Environment.GetEnvironmentVariable("CATALOG_AVATAR_PROFILE_TIMEOUT_MS") ?? "[unset]");
app.Logger.LogInformation("[startup-config] {Key}={Value}", "ASPNETCORE_URLS",
    Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "[unset]");
app.Logger.LogInformation("[startup-config] {Key}={Value}", "PORT",
    Environment.GetEnvironmentVariable("PORT") ?? "[unset]");

// Ensure market routes schema
using (var scope = app.Services.CreateScope())
{
    var routeStore = scope.ServiceProvider.GetRequiredService<IMarketRouteStore>();
    await routeStore.EnsureSchemaAsync(CancellationToken.None);
}

// Listen on configurable port: use ASPNETCORE_URLS if provided, otherwise
// fall back to PORT env or default to 5084. This allows docker-compose to
// override the listening port easily while keeping a sensible default.
var urlsEnv = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
bool hasUrlsEnv = !string.IsNullOrWhiteSpace(urlsEnv);
if (!hasUrlsEnv)
{
    string? portStr = Environment.GetEnvironmentVariable("PORT");
    if (!int.TryParse(portStr, out var port) || port <= 0 || port > 65535)
    {
        port = 5084;
    }
    app.Urls.Add($"http://0.0.0.0:{port}");
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Circles Market API v1");
        c.RoutePrefix = "swagger"; // UI at /swagger
    });
}

app.UseCors("AllowAll");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

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

app.MapCartApi();
app.MapAuthApi();
app.MapPinApi();
app.MapInventoryApi();
app.MapCanonicalizeApi();

app.Run();
