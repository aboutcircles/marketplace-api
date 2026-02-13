using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Circles.Market.Api.Auth;

/// <summary>
/// JWT authentication using RS256/JWKS from the centralized auth service (DigitalOcean).
/// Registers as the default "Bearer" scheme. Clients authenticate via the auth-service
/// challenge/verify flow and send: Authorization: Bearer &lt;auth-service-jwt&gt;
///
/// Requires AUTH_SERVICE_URL to be set. The JWKS public keys are fetched from
/// {AUTH_SERVICE_URL}/.well-known/jwks.json and cached for 10 minutes.
/// </summary>
public static class AuthServiceJwksExtensions
{
    /// <summary>
    /// Adds auth-service JWKS as the default Bearer authentication scheme.
    /// Requires AUTH_SERVICE_URL to be set.
    /// </summary>
    public static void AddAuthServiceJwks(this IServiceCollection services)
    {
        string authServiceUrl = Environment.GetEnvironmentVariable("AUTH_SERVICE_URL")
            ?? throw new InvalidOperationException("AUTH_SERVICE_URL is required");

        string issuer = Environment.GetEnvironmentVariable("AUTH_JWT_ISSUER") ?? "circles-auth";
        string audience = Environment.GetEnvironmentVariable("AUTH_JWT_AUDIENCE") ?? "market-api";
        string jwksUrl = $"{authServiceUrl.TrimEnd('/')}/.well-known/jwks.json";

        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<JwksKeyManager>>();
            return new JwksKeyManager(jwksUrl, logger);
        });

        services.AddSingleton<IPostConfigureOptions<JwtBearerOptions>, AuthServiceJwtPostConfigure>();

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

        services.AddAuthorization();
    }
}

/// <summary>
/// Post-configures JwtBearerOptions to wire up the JWKS key manager for the default Bearer scheme.
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
        if (name != JwtBearerDefaults.AuthenticationScheme)
            return;

        options.TokenValidationParameters.IssuerSigningKeyResolver =
            _keyManager.ResolveSigningKeys;
    }
}
