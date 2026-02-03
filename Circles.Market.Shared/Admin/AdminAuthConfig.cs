using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Circles.Profiles.Interfaces;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Nethereum.Hex.HexConvertors.Extensions;

namespace Circles.Market.Shared.Admin;

public static class AdminAuthConfig
{
    public static void AddAdminJwtValidation(this IServiceCollection services)
    {
        string secret = Environment.GetEnvironmentVariable("ADMIN_JWT_SECRET")
                         ?? throw new Exception("ADMIN_JWT_SECRET env variable is required for admin auth.");
        string issuer = Environment.GetEnvironmentVariable("ADMIN_JWT_ISSUER") ?? "Circles.Market.Admin";
        string audience = Environment.GetEnvironmentVariable("ADMIN_JWT_AUDIENCE") ?? "market-admin";

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));

        services.AddAuthentication()
            .AddJwtBearer(AdminAuthConstants.Scheme, options =>
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
            });

        services.AddAuthorization();
    }

    public static void AddAdminAuthIssuer(this IServiceCollection services, string postgresConnection)
    {
        if (string.IsNullOrWhiteSpace(postgresConnection))
            throw new ArgumentException("POSTGRES_CONNECTION is required", nameof(postgresConnection));

        AddAdminJwtValidation(services);

        string secret = Environment.GetEnvironmentVariable("ADMIN_JWT_SECRET")
                         ?? throw new Exception("ADMIN_JWT_SECRET env variable is required for admin auth.");
        string issuer = Environment.GetEnvironmentVariable("ADMIN_JWT_ISSUER") ?? "Circles.Market.Admin";
        string audience = Environment.GetEnvironmentVariable("ADMIN_JWT_AUDIENCE") ?? "market-admin";
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));

        services.AddSingleton<IAdminTokenService>(_ => new JwtAdminTokenService(key, issuer, audience));
        services.AddSingleton<IAdminAuthChallengeStore>(sp =>
            new PostgresAdminAuthChallengeStore(postgresConnection, sp.GetRequiredService<ILogger<PostgresAdminAuthChallengeStore>>()));
    }

    public static HashSet<string> LoadAdminAllowlist()
    {
        var raw = Environment.GetEnvironmentVariable("ADMIN_ADDRESSES") ?? string.Empty;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var addr = part.Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(addr)) set.Add(addr);
        }
        return set;
    }

    public static void EnsureAdminAllowed(HashSet<string> allowlist, string address)
    {
        if (allowlist.Count == 0)
            throw new InvalidOperationException("ADMIN_ADDRESSES must include at least one address.");

        if (!allowlist.Contains(address.Trim().ToLowerInvariant()))
            throw new UnauthorizedAccessException("Address not allowlisted for admin access.");
    }
}

public sealed class JwtAdminTokenService : IAdminTokenService
{
    private readonly SymmetricSecurityKey _key;
    private readonly string _issuer;
    private readonly string _audience;

    public JwtAdminTokenService(SymmetricSecurityKey key, string issuer, string audience)
    {
        _key = key;
        _issuer = issuer;
        _audience = audience;
    }

    public string Issue(AdminTokenSubject subject, TimeSpan lifetime)
    {
        var now = DateTimeOffset.UtcNow;
        var creds = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject.Sub),
            new("addr", subject.Address.ToLowerInvariant()),
            new("chainId", subject.ChainId.ToString())
        };

        var jwt = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: now.Add(lifetime).UtcDateTime,
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }
}

internal static class AdminSiweMessageBuilder
{
    public static (string message, string nonce, DateTimeOffset issuedAt, DateTimeOffset expiresAt) Build(
        string domain, string uri, string statement, string address, long chainId, TimeSpan ttl)
    {
        string nonce = CreateNonce(16);
        var now = DateTimeOffset.UtcNow;
        var exp = now.Add(ttl);
        var msg = $"{domain} wants you to sign in with your Ethereum account:\n" +
                  $"{address}\n\n" +
                  $"{statement}\n\n" +
                  $"URI: {uri}\n" +
                  $"Version: 1\n" +
                  $"Chain ID: {chainId}\n" +
                  $"Nonce: {nonce}\n" +
                  $"Issued At: {now:O}\n" +
                  $"Expiration Time: {exp:O}";
        return (msg, nonce, now, exp);
    }

    private static string CreateNonce(int bytes)
    {
        Span<byte> buf = stackalloc byte[bytes];
        RandomNumberGenerator.Fill(buf);
        return Convert.ToHexString(buf);
    }
}

public static class AdminAuthEndpoints
{
    public static IEndpointRouteBuilder MapAdminAuthApi(this IEndpointRouteBuilder app, string adminBasePath)
    {
        app.MapPost($"{adminBasePath}/auth/challenge", CreateChallenge)
            .WithSummary("Create an admin auth challenge (SIWE-like message)");
        app.MapPost($"{adminBasePath}/auth/verify", Verify)
            .WithSummary("Verify admin challenge signature and issue admin JWT");
        return app;
    }

    private static async Task<IResult> CreateChallenge(HttpContext ctx, IAdminAuthChallengeStore store)
    {
        ctx.Response.ContentType = AdminAuthConstants.ContentType;
        try
        {
            var req = await JsonSerializer.DeserializeAsync<AdminChallengeRequest>(
                ctx.Request.Body,
                Circles.Profiles.Models.JsonSerializerOptions.JsonLd,
                ctx.RequestAborted) ?? new AdminChallengeRequest();

            string addr = req.Address.Trim().ToLowerInvariant();
            if (!Regex.IsMatch(addr, "^0x[0-9a-f]{40}$"))
            {
                return Results.Json(new { error = "address must be a 0x hex address" }, contentType: AdminAuthConstants.ContentType, statusCode: StatusCodes.Status400BadRequest);
            }

            if (req.ChainId <= 0)
            {
                return Results.Json(new { error = "chainId must be > 0" }, contentType: AdminAuthConstants.ContentType, statusCode: StatusCodes.Status400BadRequest);
            }

            long chainId = req.ChainId;
            var ttl = TimeSpan.FromMinutes(Math.Clamp(req.ExpirationMinutes ?? 10, 1, 30));

            var (domain, baseUri) = ResolveBaseUriAndValidate(ctx);
            string uri = baseUri.GetLeftPart(UriPartial.Authority);
            string statement = string.IsNullOrWhiteSpace(req.Statement) ? "Sign in as admin" : req.Statement!;

            var (message, nonce, issuedAt, expiresAt) = AdminSiweMessageBuilder.Build(domain, uri, statement, addr, chainId, ttl);

            var ch = new AdminAuthChallenge
            {
                Id = Guid.NewGuid(),
                Address = addr,
                ChainId = chainId,
                Nonce = nonce,
                Message = message,
                IssuedAt = issuedAt,
                ExpiresAt = expiresAt,
                UserAgent = ctx.Request.Headers["User-Agent"].ToString(),
                Ip = ctx.Connection.RemoteIpAddress?.ToString()
            };

            await store.SaveAsync(ch, ctx.RequestAborted);
            return Results.Json(new AdminChallengeResponse
            {
                ChallengeId = ch.Id,
                Message = message,
                Nonce = nonce,
                ExpiresAt = expiresAt
            }, Circles.Profiles.Models.JsonSerializerOptions.JsonLd, contentType: AdminAuthConstants.ContentType);
        }
        catch (ArgumentException ex)
        {
            return Results.Json(new { error = ex.Message }, contentType: AdminAuthConstants.ContentType, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> Verify(
        HttpContext ctx,
        AdminVerifyRequest req,
        IAdminAuthChallengeStore store,
        ISafeBytesVerifier safeVerifier,
        IAdminTokenService tokens,
        CancellationToken ct)
    {
        ctx.Response.ContentType = AdminAuthConstants.ContentType;

        var log = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("AdminAuthVerify");

        var ch = await store.GetAsync(req.ChallengeId, ct);
        if (ch is null)
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        if (ch.UsedAt is not null || ch.ExpiresAt < DateTimeOffset.UtcNow)
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        if (string.IsNullOrWhiteSpace(req.Signature) || !req.Signature.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        byte[] sigBytes;
        try { sigBytes = req.Signature.HexToByteArray(); }
        catch { return Results.StatusCode(StatusCodes.Status401Unauthorized); }

        string message = ch.Message;
        byte[] messageBytes = Encoding.UTF8.GetBytes(message);

        bool ok;
        try
        {
            ok = await safeVerifier.Verify1271WithBytesAsync(messageBytes, ch.Address, sigBytes, ct);
        }
        catch (HttpRequestException ex)
        {
            log.LogError(ex, "Admin verification transport error (RPC)");
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
        catch (IOException ex)
        {
            log.LogError(ex, "Admin verification I/O error");
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            log.LogError(ex, "Admin verification timeout");
            return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Admin verification internal error");
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }

        if (!ok)
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        var marked = await store.TryMarkUsedAsync(ch.Id, ct);
        if (!marked)
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        var allowlist = AdminAuthConfig.LoadAdminAllowlist();
        try
        {
            AdminAuthConfig.EnsureAdminAllowed(allowlist, ch.Address);
        }
        catch (InvalidOperationException ex)
        {
            log.LogWarning(ex, "Admin allowlist not configured");
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        var token = tokens.Issue(new AdminTokenSubject(ch.Address, ch.ChainId), TimeSpan.FromMinutes(15));
        return Results.Json(new AdminVerifyResponse
        {
            Token = token,
            Address = ch.Address,
            ChainId = ch.ChainId,
            ExpiresIn = 900
        }, Circles.Profiles.Models.JsonSerializerOptions.JsonLd, contentType: AdminAuthConstants.ContentType);
    }

    public static (string domain, Uri baseUri) ResolveBaseUriAndValidate(HttpContext ctx)
    {
        string? allowed = Environment.GetEnvironmentVariable("ADMIN_AUTH_ALLOWED_DOMAINS");
        if (string.IsNullOrWhiteSpace(allowed))
            throw new ArgumentException("ADMIN_AUTH_ALLOWED_DOMAINS must be configured");

        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in allowed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            hosts.Add(h);
        }
        bool allowAny = hosts.Contains("*");

        string? baseUrl = Environment.GetEnvironmentVariable("ADMIN_PUBLIC_BASE_URL");
        Uri baseUri;
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("ADMIN_PUBLIC_BASE_URL must be configured");

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out baseUri!))
            throw new ArgumentException("ADMIN_PUBLIC_BASE_URL must be an absolute URI");

        string domain = baseUri.Host;
        if (!allowAny && !hosts.Contains(domain))
            throw new ArgumentException("Requested host is not allowlisted");

        return (domain, baseUri);
    }
}
