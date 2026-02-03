using System.Text;
using Circles.Profiles.Interfaces;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Circles.Market.Auth.Siwe;

public static class SiweAuthServiceCollectionExtensions
{
    public static IServiceCollection AddSiweJwtAuth(
        this IServiceCollection services,
        SiweAuthOptions options,
        string? authenticationScheme = null)
    {
        string secret = Environment.GetEnvironmentVariable(options.JwtSecretEnv)
                         ?? throw new Exception($"{options.JwtSecretEnv} env variable is required for auth.");
        string issuer = Environment.GetEnvironmentVariable(options.JwtIssuerEnv) ?? "Circles.Market";
        string audience = Environment.GetEnvironmentVariable(options.JwtAudienceEnv) ?? "market-api";

        var key = JwtTokenService.BuildKey(secret);

        if (string.IsNullOrWhiteSpace(authenticationScheme))
        {
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(o => ConfigureJwt(o, issuer, audience, key));
        }
        else
        {
            services.AddAuthentication()
                .AddJwtBearer(authenticationScheme, o => ConfigureJwt(o, issuer, audience, key));
        }

        services.AddAuthorization();
        services.AddSingleton<ITokenService>(_ => new JwtTokenService(key, issuer, audience));
        services.AddSingleton(options);
        return services;
    }

    public static IServiceCollection AddSiweAuthService(
        this IServiceCollection services,
        SiweAuthOptions options,
        Func<IServiceProvider, IAuthChallengeStore> storeFactory,
        Func<string, string>? addressNormalizer = null,
        Func<string, bool>? addressValidator = null)
    {
        services.AddSingleton(options);
        services.AddSingleton<IAuthChallengeStore>(storeFactory);
        services.AddSingleton(sp => new SiweAuthService(
            options,
            sp.GetRequiredService<IAuthChallengeStore>(),
            sp.GetRequiredService<ISafeBytesVerifier>(),
            sp.GetRequiredService<ITokenService>(),
            sp.GetRequiredService<ILoggerFactory>(),
            addressNormalizer,
            addressValidator));
        return services;
    }

    /// <summary>
    /// Adds JWT validation only (no token issuance). Use this for adapters that validate
    /// admin JWTs minted by Market but do not issue their own tokens.
    /// Uses default admin auth environment variables.
    /// </summary>
    public static IServiceCollection AddAdminJwtValidation(this IServiceCollection services)
    {
        var options = new SiweAuthOptions
        {
            JwtSecretEnv = "ADMIN_JWT_SECRET",
            JwtIssuerEnv = "ADMIN_JWT_ISSUER",
            JwtAudienceEnv = "ADMIN_JWT_AUDIENCE"
        };
        return AddAdminJwtValidation(services, options, null);
    }

    /// <summary>
    /// Adds JWT validation only (no token issuance). Use this for adapters that validate
    /// admin JWTs minted by Market but do not issue their own tokens.
    /// </summary>
    public static IServiceCollection AddAdminJwtValidation(
        this IServiceCollection services,
        SiweAuthOptions options,
        string? authenticationScheme = null)
    {
        string secret = Environment.GetEnvironmentVariable(options.JwtSecretEnv)
                        ?? throw new Exception($"{options.JwtSecretEnv} env variable is required for auth.");
        string issuer = Environment.GetEnvironmentVariable(options.JwtIssuerEnv) ?? "Circles.Market";
        string audience = Environment.GetEnvironmentVariable(options.JwtAudienceEnv) ?? "market-api";

        var key = JwtTokenService.BuildKey(secret);

        if (string.IsNullOrWhiteSpace(authenticationScheme))
        {
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(o => ConfigureJwt(o, issuer, audience, key));
        }
        else
        {
            services.AddAuthentication()
                .AddJwtBearer(authenticationScheme, o => ConfigureJwt(o, issuer, audience, key));
        }

        services.AddAuthorization();
        return services;
    }

    private static void ConfigureJwt(JwtBearerOptions options, string issuer, string audience, SymmetricSecurityKey key)
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ValidIssuer = issuer,
            ValidAudience = audience,
            IssuerSigningKey = key,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    }
}
