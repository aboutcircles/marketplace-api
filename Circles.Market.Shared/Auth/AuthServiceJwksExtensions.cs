using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Circles.Market.Shared.Auth;

/// <summary>
/// Registers auth-service JWKS (RS256) as the default Bearer authentication scheme.
/// Requires AUTH_SERVICE_URL environment variable.
/// </summary>
public static class AuthServiceJwksExtensions
{
    /// <summary>
    /// Registers auth-service JWKS auth.
    /// </summary>
    /// <param name="validAudiences">
    /// Audiences this app accepts in the JWT <c>aud</c> claim. A token is accepted if its
    /// <c>aud</c> claim contains ANY of these. The public app should pass
    /// <c>["market-api"]</c>; the admin app should pass <c>["market-admin-api"]</c>.
    /// </param>
    /// <param name="exchangeAudiences">
    /// Audiences requested when falling back to the auth-service /exchange endpoint
    /// (federated tokens, e.g. Gnosis App). The public app should pass <c>["market-api"]</c>;
    /// the admin app should pass <c>["market-admin-api"]</c>. Admin tokens are deliberately
    /// single-audience; admins federate separately for the public surface if needed.
    /// </param>
    public static void AddAuthServiceJwks(
        this IServiceCollection services,
        string[] validAudiences,
        string[] exchangeAudiences)
    {
        if (validAudiences.Length == 0)
            throw new ArgumentException("validAudiences must not be empty", nameof(validAudiences));
        if (exchangeAudiences.Length == 0)
            throw new ArgumentException("exchangeAudiences must not be empty", nameof(exchangeAudiences));

        string authServiceUrl = Environment.GetEnvironmentVariable("AUTH_SERVICE_URL")
            ?? throw new InvalidOperationException("AUTH_SERVICE_URL is required");

        string issuer = Environment.GetEnvironmentVariable("AUTH_JWT_ISSUER") ?? "circles-auth";
        string jwksUrl = $"{authServiceUrl.TrimEnd('/')}/.well-known/jwks.json";

        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<JwksKeyManager>>();
            return new JwksKeyManager(jwksUrl, logger,
                cacheDuration: ReadSecondsEnv("AUTH_JWKS_CACHE_SECONDS"),
                maxStaleness: ReadSecondsEnv("AUTH_JWKS_MAX_STALE_SECONDS"));
        });

        // Keeps the JWKS snapshot warm so ResolveSigningKeys never does I/O on the
        // request path. AddHostedService dedupes by type, so a double AddAuthServiceJwks
        // call still registers a single refresher.
        services.AddHostedService(sp => new JwksRefreshService(
            sp.GetRequiredService<JwksKeyManager>(),
            sp.GetRequiredService<ILogger<JwksRefreshService>>()));

        services.AddSingleton<IPostConfigureOptions<JwtBearerOptions>, AuthServiceJwtPostConfigure>();

        // Token exchange fallback: named HttpClient + singleton service
        // AddMemoryCache is idempotent — safe to call even if already registered
        services.AddMemoryCache();
        services.AddHttpClient("token-exchange", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(5);
        });
        services.AddSingleton(sp => new TokenExchangeService(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IMemoryCache>(),
            sp.GetRequiredService<ILogger<TokenExchangeService>>(),
            authServiceUrl,
            exchangeAudiences));

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,
                    ValidIssuer = issuer,
                    ValidAudiences = validAudiences,
                    ClockSkew = TimeSpan.FromSeconds(30)
                };

                options.Events = new JwtBearerEvents
                {
                    OnAuthenticationFailed = async context =>
                    {
                        var logger = context.HttpContext.RequestServices
                            .GetRequiredService<ILoggerFactory>()
                            .CreateLogger("AuthServiceJwks");

                        // Direct RS256 validation failed. Before treating this as an
                        // auth failure, try the token-exchange fallback: federated
                        // issuers (e.g. Gnosis App) legitimately present tokens whose
                        // kid/issuer/audience don't match this JWKS, so a direct
                        // failure is expected for them. Only when the exchange also
                        // declines is the request genuinely unauthenticated — so defer
                        // logging until then, and never at WARNING with the exception,
                        // to avoid an IDX-stacktrace on every recovered federated login.
                        string? rawToken = context.HttpContext.Request.Headers.Authorization
                            .FirstOrDefault()?.Replace("Bearer ", "", StringComparison.OrdinalIgnoreCase);

                        if (!string.IsNullOrEmpty(rawToken))
                        {
                            var exchange = context.HttpContext.RequestServices
                                .GetRequiredService<TokenExchangeService>();
                            var principal = await exchange.TryExchangeAsync(
                                rawToken, context.HttpContext.RequestAborted);
                            if (principal is not null)
                            {
                                logger.LogDebug("Token exchange fallback succeeded");
                                context.Principal = principal;
                                context.Success();
                                return;
                            }
                        }

                        // Genuine failure (no bearer token, or the exchange declined):
                        // an expected 401 for a stale/expired/foreign token, not an
                        // application error. Log the reason at Debug, without the
                        // stacktrace.
                        logger.LogDebug("Auth-service JWT validation failed: {Message}",
                            context.Exception.Message);
                    }
                };
            });

        services.AddAuthorization();
    }

    /// <summary>
    /// Optional env var holding a duration in whole seconds; unset, unparsable or
    /// non-positive values fall back to the built-in default (returns null).
    /// </summary>
    private static TimeSpan? ReadSecondsEnv(string name)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out int seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }
}

internal sealed class AuthServiceJwtPostConfigure : IPostConfigureOptions<JwtBearerOptions>
{
    private readonly JwksKeyManager _keyManager;

    public AuthServiceJwtPostConfigure(JwksKeyManager keyManager)
    {
        _keyManager = keyManager;
    }

    public void PostConfigure(string? name, JwtBearerOptions options)
    {
        if (name != JwtBearerDefaults.AuthenticationScheme)
            return;

        options.TokenValidationParameters.IssuerSigningKeyResolver =
            _keyManager.ResolveSigningKeys;
    }
}
