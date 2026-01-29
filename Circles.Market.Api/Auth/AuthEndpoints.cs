using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Circles.Market.Api.Auth;

/// <summary>
/// JWT authentication configuration using RS256/JWKS from the centralized auth service.
///
/// Authentication flow:
/// 1. Client authenticates with circles-auth-service (SIWE or Passkey)
/// 2. Auth service issues JWT signed with its PRIVATE key (RS256)
/// 3. Client sends JWT to this API in Authorization header
/// 4. This API validates using PUBLIC key from auth service's JWKS endpoint
/// 5. If valid, the user is authenticated
///
/// Trust model:
/// - We trust the auth service because we trust its domain (AUTH_SERVICE_URL)
/// - We verify the JWT signature using the public key from JWKS
/// - If signature is valid, the token was definitely issued by the auth service
/// - No shared secrets needed between services
/// </summary>
public static class AuthEndpoints
{
    /// <summary>
    /// Configures JWT authentication using JWKS from the auth service.
    ///
    /// Environment variables:
    /// - AUTH_SERVICE_URL: Base URL of the auth service (default: https://staging.circlesubi.network/auth)
    /// - AUTH_JWT_ISSUER: Expected JWT issuer (default: circles-auth)
    /// - AUTH_JWT_AUDIENCE: Expected JWT audience (default: market-api)
    /// </summary>
    public static void AddJwtAuth(this IServiceCollection services)
    {
        string authServiceUrl = Environment.GetEnvironmentVariable("AUTH_SERVICE_URL")
                                ?? "https://staging.circlesubi.network/auth";
        string issuer = Environment.GetEnvironmentVariable("AUTH_JWT_ISSUER")
                        ?? "circles-auth";
        string audience = Environment.GetEnvironmentVariable("AUTH_JWT_AUDIENCE")
                          ?? "market-api";

        // Create the JWKS key manager as a singleton
        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<JwksKeyManager>>();
            return new JwksKeyManager(authServiceUrl, logger);
        });

        // Use post-configure to wire up the key resolver after services are built
        services.AddSingleton<IPostConfigureOptions<JwtBearerOptions>, JwtBearerOptionsPostConfigure>();

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
                    ValidAudience = audience,
                    ClockSkew = TimeSpan.FromSeconds(30)
                    // IssuerSigningKeyResolver is set in JwtBearerOptionsPostConfigure
                };

                // Log validation failures for debugging
                options.Events = new JwtBearerEvents
                {
                    OnAuthenticationFailed = context =>
                    {
                        var logger = context.HttpContext.RequestServices
                            .GetRequiredService<ILoggerFactory>()
                            .CreateLogger("JwtAuth");
                        logger.LogWarning(context.Exception,
                            "JWT authentication failed: {Message}", context.Exception.Message);
                        return Task.CompletedTask;
                    }
                };
            });

        services.AddAuthorization();
    }

    /// <summary>
    /// Maps auth-related endpoints. Currently empty since auth is handled by the external auth-service.
    /// Kept for backwards compatibility and potential future health/info endpoints.
    /// </summary>
    public static IEndpointRouteBuilder MapAuthApi(this IEndpointRouteBuilder app)
    {
        // No local auth endpoints - authentication is handled by the centralized auth-service.
        // Clients should authenticate via:
        // - POST {AUTH_SERVICE_URL}/challenge - get SIWE challenge
        // - POST {AUTH_SERVICE_URL}/verify - verify signature, get JWT
        // Then include the JWT in Authorization: Bearer <token> header for this API.
        return app;
    }
}

/// <summary>
/// Post-configures JwtBearerOptions to wire up the JWKS key manager after DI is built.
/// </summary>
internal sealed class JwtBearerOptionsPostConfigure : IPostConfigureOptions<JwtBearerOptions>
{
    private readonly JwksKeyManager _keyManager;

    public JwtBearerOptionsPostConfigure(JwksKeyManager keyManager)
    {
        _keyManager = keyManager;
    }

    public void PostConfigure(string? name, JwtBearerOptions options)
    {
        // Only configure the default scheme
        if (name != JwtBearerDefaults.AuthenticationScheme)
            return;

        options.TokenValidationParameters.IssuerSigningKeyResolver =
            _keyManager.ResolveSigningKeys;
    }
}
