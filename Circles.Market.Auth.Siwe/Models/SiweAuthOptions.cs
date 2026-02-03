namespace Circles.Market.Auth.Siwe;

public sealed class SiweAuthOptions
{
    public string AllowedDomainsEnv { get; init; } = "MARKET_AUTH_ALLOWED_DOMAINS";
    public string? PublicBaseUrlEnv { get; init; } = "PUBLIC_BASE_URL";
    public string? ExternalBaseUrlEnv { get; init; } = "EXTERNAL_BASE_URL";

    public string JwtSecretEnv { get; init; } = "MARKET_JWT_SECRET";
    public string JwtIssuerEnv { get; init; } = "MARKET_JWT_ISSUER";
    public string JwtAudienceEnv { get; init; } = "MARKET_JWT_AUDIENCE";

    public TimeSpan DefaultChallengeTtl { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan TokenLifetime { get; init; } = TimeSpan.FromMinutes(15);
    public int MaxChallengeMinutes { get; init; } = 30;
    public int MinChallengeMinutes { get; init; } = 1;

    public bool RequirePublicBaseUrl { get; init; } = false;

    public bool RequireAllowlist { get; init; } = false;
    public string? AllowlistEnv { get; init; }
}