using System.Data.Common;
using System.Threading.RateLimiting;
using Circles.Market.Api;
using Circles.Market.Api.Admin;
using Circles.Market.Api.Auth;
using Circles.Market.Auth.Siwe;
using Circles.Market.Api.Cart;
using Circles.Market.Api.Cart.Validation;
using Circles.Market.Api.Fulfillment;
using Circles.Market.Api.Inventory;
using Circles.Market.Api.Payments;
using Circles.Market.Api.Pin;
using Circles.Market.Api.Routing;
using Circles.Market.Api.Service;
using Circles.Market.Shared.Admin;
using Circles.Profiles.Interfaces;
using Circles.Profiles.Market;
using Circles.Profiles.Sdk;
using Circles.Profiles.Sdk.Utils;
using Nethereum.Web3;
using Prometheus;

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

var publicBuilder = WebApplication.CreateBuilder(args);

// Observability & config
publicBuilder.Logging.ClearProviders();
publicBuilder.Logging.AddConsole();

// Memory cache with a global size cap (200 MiB)
publicBuilder.Services.AddMemoryCache(o => o.SizeLimit = 200 * 1024 * 1024);

// Rate limiting: simple fixed-window per-IP
publicBuilder.Services.AddRateLimiter(o =>
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
publicBuilder.Services.AddSingleton<IIpfsStore>(_ => new IpfsRpcApiStore(ipfsRpcUrl, ipfsRpcBearer, ipfsGatewayUrl));
publicBuilder.Services.Decorate<IIpfsStore, CachingIpfsStore>();

// Chain + registry
publicBuilder.Services.AddSingleton<INameRegistry>(_ => new NameRegistry(chainRpcUrl));
publicBuilder.Services.AddSingleton<IChainApi>(_ =>
    new EthereumChainApi(new Web3(chainRpcUrl), Helpers.DefaultChainId));

// Signature verification:
// Register one DefaultSignatureVerifier instance with logging and expose it as both interfaces.
publicBuilder.Services.AddSingleton<DefaultSignatureVerifier>(sp =>
    new DefaultSignatureVerifier(
        sp.GetRequiredService<IChainApi>(),
        sp.GetRequiredService<ILogger<DefaultSignatureVerifier>>()));
publicBuilder.Services.AddSingleton<ISignatureVerifier>(sp =>
    sp.GetRequiredService<DefaultSignatureVerifier>());
publicBuilder.Services.AddSingleton<ISafeBytesVerifier>(sp =>
    sp.GetRequiredService<DefaultSignatureVerifier>());

// Cart & Order services: require Postgres
// Expect a standard Npgsql (.NET) connection string via POSTGRES_CONNECTION
string? pgConn = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION")
                 ?? throw new Exception("POSTGRES_CONNECTION env variable is required to run the service.");

if (string.IsNullOrWhiteSpace(pgConn))
    throw new Exception("POSTGRES_CONNECTION env variable is required to run the service.");

bool autoMigrate = string.Equals(Environment.GetEnvironmentVariable("DB_AUTO_MIGRATE"), "true", StringComparison.OrdinalIgnoreCase);

publicBuilder.Services.AddSingleton<IBasketStore>(sp =>
    new PostgresBasketStore(pgConn!, sp.GetRequiredService<ILogger<PostgresBasketStore>>()));

publicBuilder.Services.AddSingleton<IOrderStore>(sp =>
    new PostgresOrderStore(pgConn!, sp.GetRequiredService<ILogger<PostgresOrderStore>>()));

// Order access service (buyer-scoped reads + ownership checks)
publicBuilder.Services.AddSingleton<IOrderAccessService, OrderAccessService>();

// Payments store (schema bootstrap on construction)
publicBuilder.Services.AddSingleton<IPaymentStore>(sp =>
    new PostgresPaymentStore(pgConn!, sp.GetRequiredService<ILogger<PostgresPaymentStore>>()));

// Order payment flow: single owner of payment→order lifecycle
// SSE bus + hooks that publish status changes to subscribers
publicBuilder.Services.AddSingleton<IOrderStatusEventBus, InMemoryOrderStatusEventBus>();
publicBuilder.Services.AddSingleton<IOrderLifecycleHooks>(sp =>
    new SseOrderLifecycleHooks(
        sp.GetRequiredService<IOrderStatusEventBus>(),
        sp.GetRequiredService<IOrderStore>(),
        sp.GetRequiredService<IOrderFulfillmentClient>(),
        sp.GetRequiredService<IMarketRouteStore>(),
        sp.GetRequiredService<ILogger<SseOrderLifecycleHooks>>()));
publicBuilder.Services.AddSingleton<IOrderPaymentFlow, OrderPaymentFlow>();

// Fulfillment/Inventory HTTP clients: use named clients with no default auth headers
publicBuilder.Services.AddHttpClient();

publicBuilder.Services.AddHttpClient("fulfillment_public", client =>
{
    client.DefaultRequestHeaders.Clear();
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

publicBuilder.Services.AddHttpClient("fulfillment_trusted", client =>
{
    client.DefaultRequestHeaders.Clear();
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

publicBuilder.Services.AddHttpClient("inventory_public", client =>
{
    client.DefaultRequestHeaders.Clear();
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

publicBuilder.Services.AddHttpClient("inventory_trusted", client =>
{
    client.DefaultRequestHeaders.Clear();
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

publicBuilder.Services.AddSingleton<IOutboundServiceAuthProvider>(sp =>
    new EnvOutboundServiceAuthProvider(sp.GetRequiredService<ILogger<EnvOutboundServiceAuthProvider>>()));

publicBuilder.Services.AddSingleton<IMarketRouteStore>(sp =>
    new PostgresMarketRouteStore(
        pgConn!,
        sp.GetRequiredService<ILogger<PostgresMarketRouteStore>>(),
        sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>()));

publicBuilder.Services.AddSingleton<IOrderFulfillmentClient, HttpOrderFulfillmentClient>();

// Payments poller: polls CrcV2.PaymentReceived via circles_query and persists to Postgres
publicBuilder.Services.AddHostedService<CirclesPaymentsPoller>();

// Cart validation rules (now offer-driven: keep ItemsNonEmpty + OfferRequiredSlots only)
publicBuilder.Services.AddSingleton<ICartRule, ItemsNonEmptyRule>();
publicBuilder.Services.AddSingleton<ICartRule, OfferRequiredSlotsRule>();
publicBuilder.Services.AddSingleton<ICartRule, CustomerNameRule>();

publicBuilder.Services.AddSingleton<ICartValidator, CartValidator>();
publicBuilder.Services.AddSingleton<IProductResolver, ProductResolver>();
publicBuilder.Services.AddSingleton<IBasketCanonicalizer, BasketCanonicalizer>();

// Live inventory client for enforcing inventory limits in basket canonicalization
publicBuilder.Services.AddSingleton<ILiveInventoryClient, LiveInventoryClient>();

// One-off sales store for sold-once default availability
publicBuilder.Services.AddSingleton<IOneOffSalesStore>(sp =>
    new PostgresOneOffSalesStore(pgConn!, sp.GetRequiredService<ILogger<PostgresOneOffSalesStore>>()));

// JSON-LD shape verification for pin endpoint (allow only user-generated shapes)
publicBuilder.Services.AddSingleton<IJsonLdShapeVerifier, JsonLdShapeVerifier>();

// Aggregator service
publicBuilder.Services.AddSingleton(sp =>
    new Circles.Profiles.Aggregation.BasicAggregator(
        new TimeoutIpfsStore(
            sp.GetRequiredService<IIpfsStore>(),
            TimeSpan.FromMilliseconds(
                int.TryParse(Environment.GetEnvironmentVariable("CATALOG_AVATAR_PROFILE_TIMEOUT_MS"), out var ms) && ms > 0
                    ? ms
                    : 1000)),
        sp.GetRequiredService<INameRegistry>(),
        sp.GetRequiredService<ISignatureVerifier>()));
publicBuilder.Services.AddSingleton<CatalogReducer>();
publicBuilder.Services.AddSingleton<OperatorCatalogService>();

// OpenAPI
publicBuilder.Services.AddOpenApi();
publicBuilder.Services.AddEndpointsApiExplorer();
publicBuilder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.OpenApiInfo
    {
        Title = "Circles Market API",
        Version = "v1",
        Description = "Swagger UI for Circles.Market.Api"
    });
});

// HttpClient for dereferencing availability/inventory feeds
publicBuilder.Services.AddHttpClient();

// CORS: allow all origins/headers/methods (for demo/tooling) with exposed pagination headers
publicBuilder.Services.AddCors(options =>
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
var publicAuthOptions = new SiweAuthOptions
{
    AllowedDomainsEnv = "MARKET_AUTH_ALLOWED_DOMAINS",
    PublicBaseUrlEnv = "PUBLIC_BASE_URL",
    ExternalBaseUrlEnv = "EXTERNAL_BASE_URL",
    JwtSecretEnv = "MARKET_JWT_SECRET",
    JwtIssuerEnv = "MARKET_JWT_ISSUER",
    JwtAudienceEnv = "MARKET_JWT_AUDIENCE",
    RequirePublicBaseUrl = false,
    RequireAllowlist = false
};

publicBuilder.Services.AddSiweJwtAuth(publicAuthOptions);
publicBuilder.Services.AddSiweAuthService(
    publicAuthOptions,
    sp => new PostgresAuthChallengeStore(
        Environment.GetEnvironmentVariable("POSTGRES_CONNECTION")
        ?? throw new Exception("POSTGRES_CONNECTION env variable is required"),
        sp.GetRequiredService<ILogger<PostgresAuthChallengeStore>>()));

var publicApp = publicBuilder.Build();

// Log startup settings (redacted where needed)
publicApp.Logger.LogInformation("[startup-config] {Key}={Value}", "RPC", SafeUrl(chainRpcUrl));
publicApp.Logger.LogInformation("[startup-config] {Key}={Value}", "IPFS_RPC_URL", SafeUrl(ipfsRpcUrl));
publicApp.Logger.LogInformation("[startup-config] {Key}={Value}", "IPFS_RPC_BEARER", SafeToken(ipfsRpcBearer));
publicApp.Logger.LogInformation("[startup-config] {Key}={Value}", "IPFS_GATEWAY_URL", SafeUrl(ipfsGatewayUrl));
publicApp.Logger.LogInformation("[startup-config] {Key}={Value}", "POSTGRES_CONNECTION", SafeConnectionString(pgConn!));
publicApp.Logger.LogInformation("[startup-config] {Key}={Value}", "DB_AUTO_MIGRATE", autoMigrate);
publicApp.Logger.LogInformation("[startup-config] {Key}={Value}", "CATALOG_AVATAR_PROFILE_TIMEOUT_MS",
    Environment.GetEnvironmentVariable("CATALOG_AVATAR_PROFILE_TIMEOUT_MS") ?? "[unset]");
publicApp.Logger.LogInformation("[startup-config] {Key}={Value}", "ASPNETCORE_URLS",
    Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "[unset]");
publicApp.Logger.LogInformation("[startup-config] {Key}={Value}", "PORT",
    Environment.GetEnvironmentVariable("PORT") ?? "[unset]");

// Ensure market routes schema
using (var scope = publicApp.Services.CreateScope())
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
    publicApp.Urls.Add($"http://0.0.0.0:{port}");
}

if (publicApp.Environment.IsDevelopment())
{
    publicApp.MapOpenApi();
    publicApp.UseSwagger();
    publicApp.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Circles Market API v1");
        c.RoutePrefix = "swagger"; // UI at /swagger
    });
}

publicApp.UseCors("AllowAll");
publicApp.UseRateLimiter();
publicApp.UseAuthentication();
publicApp.UseAuthorization();
publicApp.UseHttpMetrics();

// Health endpoint for container orchestration
publicApp.MapServiceApi();

publicApp.MapCartApi();
publicApp.MapSiweAuthApi("/api/auth", "Sign in to Circles Market", MarketConstants.ContentTypes.JsonLdUtf8);
publicApp.MapPinApi();
publicApp.MapInventoryApi();
publicApp.MapCanonicalizeApi();
publicApp.MapMetrics();

var adminBuilder = WebApplication.CreateBuilder(args);
adminBuilder.Logging.ClearProviders();
adminBuilder.Logging.AddConsole();

var adminAuthOptions = new SiweAuthOptions
{
    AllowedDomainsEnv = "ADMIN_AUTH_ALLOWED_DOMAINS",
    PublicBaseUrlEnv = "ADMIN_PUBLIC_BASE_URL",
    JwtSecretEnv = "ADMIN_JWT_SECRET",
    JwtIssuerEnv = "ADMIN_JWT_ISSUER",
    JwtAudienceEnv = "ADMIN_JWT_AUDIENCE",
    RequirePublicBaseUrl = true,
    RequireAllowlist = true,
    AllowlistEnv = "ADMIN_ADDRESSES"
};

// Admin CORS: dev-friendly, prod-safe
var adminCorsOrigins = Environment.GetEnvironmentVariable("ADMIN_CORS_ALLOWED_ORIGINS");
if (!string.IsNullOrWhiteSpace(adminCorsOrigins))
{
    adminBuilder.Services.AddCors(options =>
    {
        options.AddPolicy("AdminCors", policy =>
        {
            policy.WithOrigins(adminCorsOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .AllowAnyMethod()
                .AllowAnyHeader();
        });
    });
}
else if (adminBuilder.Environment.IsDevelopment())
{
    adminBuilder.Services.AddCors(options =>
    {
        options.AddPolicy("AdminCors", policy =>
        {
            policy.WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
                .AllowAnyMethod()
                .AllowAnyHeader();
        });
    });
}

adminBuilder.Services.AddSiweJwtAuth(adminAuthOptions, AdminAuthConstants.Scheme);
adminBuilder.Services.AddSiweAuthService(
    adminAuthOptions,
    sp => new PostgresAuthChallengeStore(
        pgConn!,
        sp.GetRequiredService<ILogger<PostgresAuthChallengeStore>>(),
        tableName: "admin_auth_challenges"),
    addressNormalizer: AddressUtils.NormalizeToLowercase,
    addressValidator: AddressUtils.IsValidLowercaseAddress);
adminBuilder.Services.AddAdminSignatureVerifier();

int adminPort = AdminPortConfig.GetAdminPort("MARKET_ADMIN_PORT", 5090);
adminBuilder.WebHost.UseUrls($"http://0.0.0.0:{adminPort}");

string odooAdminUrl = Environment.GetEnvironmentVariable("ODOO_ADMIN_INTERNAL_URL")
                       ?? throw new Exception("ODOO_ADMIN_INTERNAL_URL env variable is required for admin proxy.");
string codeDispAdminUrl = Environment.GetEnvironmentVariable("CODEDISP_ADMIN_INTERNAL_URL")
                           ?? throw new Exception("CODEDISP_ADMIN_INTERNAL_URL env variable is required for admin proxy.");
string adminProxyHostsRaw = Environment.GetEnvironmentVariable("ADMIN_PROXY_ALLOWED_HOSTS")
                            ?? throw new Exception("ADMIN_PROXY_ALLOWED_HOSTS env variable is required for admin proxy.");
var adminProxyHosts = new HashSet<string>(adminProxyHostsRaw
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(h => h.Trim()), StringComparer.OrdinalIgnoreCase);

static Uri RequireSafeAdminUri(string raw, HashSet<string> allowedHosts)
{
    if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        throw new Exception($"Admin internal URL is invalid: {raw}");
    if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase))
        throw new Exception($"Admin internal URL must use http scheme: {raw}");
    if (!allowedHosts.Contains(uri.Host))
        throw new Exception($"Admin internal URL host '{uri.Host}' is not in ADMIN_PROXY_ALLOWED_HOSTS");
    return uri;
}

var odooAdminUri = RequireSafeAdminUri(odooAdminUrl, adminProxyHosts);
var codeDispAdminUri = RequireSafeAdminUri(codeDispAdminUrl, adminProxyHosts);

adminBuilder.Services.AddHttpClient("odoo-admin", client =>
{
    client.BaseAddress = odooAdminUri;
    client.Timeout = TimeSpan.FromSeconds(10);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AllowAutoRedirect = false
});

adminBuilder.Services.AddHttpClient("codedisp-admin", client =>
{
    client.BaseAddress = codeDispAdminUri;
    client.Timeout = TimeSpan.FromSeconds(10);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AllowAutoRedirect = false
});

var adminApp = adminBuilder.Build();

// Enable CORS for admin app (only if configured or in dev)
var adminCorsConfigured = !string.IsNullOrWhiteSpace(adminCorsOrigins) || adminApp.Environment.IsDevelopment();
if (adminCorsConfigured)
{
    adminApp.UseCors("AdminCors");
}

adminApp.UseAuthentication();
adminApp.UseAuthorization();

adminApp.MapSiweAuthApi("/admin/auth", "Sign in as admin", AdminAuthConstants.ContentType);
adminApp.MapMarketAdminApi("/admin", pgConn!);

var publicTask = publicApp.RunAsync();
var adminTask = adminApp.RunAsync();
await Task.WhenAll(publicTask, adminTask);
