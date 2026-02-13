using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Circles.Market.Api.Auth;

/// <summary>
/// Opt-in JWT authentication using RS256/JWKS from the centralized auth service.
/// Registers as a second named scheme ("AuthService") alongside the existing SIWE "Bearer" scheme.
///
/// When AUTH_SERVICE_URL is set, both schemes are active. Clients can authenticate via:
/// - Authorization: Bearer &lt;local-siwe-jwt&gt;  (HS256, existing SIWE flow)
/// - Authorization: AuthService &lt;auth-service-jwt&gt;  (RS256, auth-service JWKS)
/// </summary>
public static class AuthServiceJwksExtensions
{
    public const string AuthServiceScheme = "AuthService";

    /// <summary>
    /// Adds auth-service JWKS as a second authentication scheme if AUTH_SERVICE_URL is configured.
    /// Returns true if the scheme was registered, false if AUTH_SERVICE_URL is not set.
    /// </summary>
    public static bool TryAddAuthServiceJwks(this IServiceCollection services)
    {
        string? authServiceUrl = Environment.GetEnvironmentVariable("AUTH_SERVICE_URL");
        if (string.IsNullOrWhiteSpace(authServiceUrl))
            return false;

        string issuer = Environment.GetEnvironmentVariable("AUTH_JWT_ISSUER") ?? "circles-auth";
        string audience = Environment.GetEnvironmentVariable("AUTH_JWT_AUDIENCE") ?? "market-api";
        string jwksUrl = $"{authServiceUrl.TrimEnd('/')}/.well-known/jwks.json";

        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<JwksKeyManager>>();
            return new JwksKeyManager(jwksUrl, logger);
        });

        services.AddSingleton<IPostConfigureOptions<JwtBearerOptions>, AuthServiceJwtPostConfigure>();

        services.AddAuthentication()
            .AddJwtBearer(AuthServiceScheme, options =>
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
                    // IssuerSigningKeyResolver is set in AuthServiceJwtPostConfigure
                };

                options.Events = new JwtBearerEvents
                {
                    OnAuthenticationFailed = context =>
                    {
                        var logger = context.HttpContext.RequestServices
                            .GetRequiredService<ILoggerFactory>()
                            .CreateLogger("AuthServiceJwks");
                        logger.LogWarning(context.Exception,
                            "Auth-service JWT validation failed: {Message}", context.Exception.Message);
                        return Task.CompletedTask;
                    }
                };
            });

        return true;
    }
}

/// <summary>
/// Post-configures JwtBearerOptions to wire up the JWKS key manager for the AuthService scheme.
/// </summary>
internal sealed class AuthServiceJwtPostConfigure : IPostConfigureOptions<JwtBearerOptions>
{
    private readonly JwksKeyManager _keyManager;

    public AuthServiceJwtPostConfigure(JwksKeyManager keyManager)
    {
        _keyManager = keyManager;
    }

    public void PostConfigure(string? name, JwtBearerOptions options)
    {
        if (name != AuthServiceJwksExtensions.AuthServiceScheme)
            return;

        options.TokenValidationParameters.IssuerSigningKeyResolver =
            _keyManager.ResolveSigningKeys;
    }
}
