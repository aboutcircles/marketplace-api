using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Circles.Profiles.Interfaces;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Nethereum.Hex.HexConvertors.Extensions;

namespace Circles.Market.Api.Auth;

public static class AuthEndpoints
{
    private const string ContentType = MarketConstants.ContentTypes.JsonLdUtf8;

    public static void AddJwtAuth(this IServiceCollection services)
    {
        string secret = Environment.GetEnvironmentVariable("MARKET_JWT_SECRET")
                         ?? throw new Exception("MARKET_JWT_SECRET env variable is required for auth.");
        string issuer = Environment.GetEnvironmentVariable("MARKET_JWT_ISSUER") ?? "Circles.Market";
        string audience = Environment.GetEnvironmentVariable("MARKET_JWT_AUDIENCE") ?? "market-api";

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));

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
                    IssuerSigningKey = key,
                    ClockSkew = TimeSpan.FromSeconds(30)
                };
            });

        services.AddAuthorization();
        services.AddSingleton<ITokenService>(_ => new JwtTokenService(key, issuer, audience));
        services.AddSingleton<IAuthChallengeStore>(sp =>
            new PostgresAuthChallengeStore(
                Environment.GetEnvironmentVariable("POSTGRES_CONNECTION")
                ?? throw new Exception("POSTGRES_CONNECTION env variable is required"),
                sp.GetRequiredService<ILogger<PostgresAuthChallengeStore>>()));
    }

    public static IEndpointRouteBuilder MapAuthApi(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/auth");

        g.MapPost("/challenge", CreateChallenge)
            .WithSummary("Create an auth challenge (SIWE-like message)")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest);

        g.MapPost("/verify", Verify)
            .WithSummary("Verify challenge signature and issue JWT")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized);

        return app;
    }

    private static async Task<IResult> CreateChallenge(HttpContext ctx, IAuthChallengeStore store)
    {
        ctx.Response.ContentType = ContentType;
        try
        {
            var req = await JsonSerializer.DeserializeAsync<ChallengeRequest>(ctx.Request.Body,
                Circles.Profiles.Models.JsonSerializerOptions.JsonLd, ctx.RequestAborted) ?? new ChallengeRequest();

            string addr = Utils.NormalizeAddr(req.Address);
            long chainId = req.ChainId <= 0 ? MarketConstants.Defaults.ChainId : req.ChainId;
            var ttl = TimeSpan.FromMinutes(Math.Clamp(req.ExpirationMinutes ?? 10, 1, 30));

            // Derive server-controlled base URI and enforce allowlisted domain
            var (domain, baseUri) = MarketAuthConfig.ResolveBaseUriAndValidate(ctx);
            string uri = baseUri.GetLeftPart(UriPartial.Authority);
            string statement = string.IsNullOrWhiteSpace(req.Statement) ? "Sign in to Circles Market" : req.Statement!;

            var (message, nonce, issuedAt, expiresAt) = SiweMessageBuilder.Build(domain, uri, statement, addr, chainId, ttl);

            var ch = new AuthChallenge
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
            return Results.Json(new ChallengeResponse
            {
                ChallengeId = ch.Id,
                Message = message,
                Nonce = nonce,
                ExpiresAt = expiresAt
            }, Circles.Profiles.Models.JsonSerializerOptions.JsonLd, contentType: ContentType);
        }
        catch (ArgumentException ex)
        {
            return Results.Json(new { error = ex.Message }, contentType: ContentType, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> Verify(
        HttpContext ctx,
        VerifyRequest req,
        IAuthChallengeStore store,
        ISafeBytesVerifier safeVerifier,
        ITokenService tokens,
        CancellationToken ct)
    {
        ctx.Response.ContentType = ContentType;

        var log = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("AuthVerify");

        var ch = await store.GetAsync(req.ChallengeId, ct);
        if (ch is null)
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        if (ch.UsedAt is not null || ch.ExpiresAt < DateTimeOffset.UtcNow)
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        // Signature must be 0x… hex
        if (string.IsNullOrWhiteSpace(req.Signature) || !req.Signature.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        byte[] sigBytes;
        try { sigBytes = req.Signature.HexToByteArray(); }
        catch { return Results.StatusCode(StatusCodes.Status401Unauthorized); }

        string message = ch.Message;
        byte[] messageBytes = Encoding.UTF8.GetBytes(message);

        // Use the shared verifier which supports both EOAs (keccak(bytes)) and ERC-1271(bytes)
        bool ok;
        try
        {
            ok = await safeVerifier.Verify1271WithBytesAsync(messageBytes, ch.Address, sigBytes, ct);
        }
        catch (HttpRequestException ex)
        {
            log.LogError(ex, "Verification transport error (RPC)");
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
        catch (IOException ex)
        {
            log.LogError(ex, "Verification I/O error");
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            log.LogError(ex, "Verification timeout");
            return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
        }
        catch (Exception ex)
        {
            // Unexpected verifier failure – surface as 500 instead of 401 to avoid masking operational issues
            log.LogError(ex, "Verification internal error");
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }

        if (!ok)
        {
            // Signature checked but did not verify
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        // Atomic mark-used: only the first successful verification can mint a token
        var marked = await store.TryMarkUsedAsync(ch.Id, ct);
        if (!marked)
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        var token = tokens.Issue(new TokenSubject(ch.Address, ch.ChainId), TimeSpan.FromMinutes(15));
        return Results.Json(new VerifyResponse
        {
            Token = token,
            Address = ch.Address,
            ChainId = ch.ChainId,
            ExpiresIn = 900
        }, Circles.Profiles.Models.JsonSerializerOptions.JsonLd, contentType: ContentType);
    }
}

internal static class SiweMessageBuilder
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

public interface ITokenService
{
    string Issue(TokenSubject subject, TimeSpan lifetime);
}

public readonly record struct TokenSubject(string Address, long ChainId)
{
    public string Sub => $"{Address.ToLowerInvariant()}@{ChainId}";
}

public sealed class JwtTokenService : ITokenService
{
    private readonly SymmetricSecurityKey _key;
    private readonly string _issuer;
    private readonly string _audience;

    public JwtTokenService(SymmetricSecurityKey key, string issuer, string audience)
    {
        _key = key; _issuer = issuer; _audience = audience;
    }

    public string Issue(TokenSubject subject, TimeSpan lifetime)
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

internal static class MarketAuthConfig
{
    // Returns the allowlisted host (domain) and a base URI to use in SIWE message
    public static (string domain, Uri baseUri) ResolveBaseUriAndValidate(HttpContext ctx)
    {
        string? allowed = Environment.GetEnvironmentVariable("MARKET_AUTH_ALLOWED_DOMAINS");
        if (string.IsNullOrWhiteSpace(allowed))
            throw new ArgumentException("MARKET_AUTH_ALLOWED_DOMAINS must be configured");

        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in allowed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            hosts.Add(h);
        }
        bool allowAny = hosts.Contains("*");

        string? baseUrl = Environment.GetEnvironmentVariable("PUBLIC_BASE_URL")
                           ?? Environment.GetEnvironmentVariable("EXTERNAL_BASE_URL");
        Uri baseUri;
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out baseUri!))
                throw new ArgumentException("PUBLIC_BASE_URL/EXTERNAL_BASE_URL must be an absolute URI");
        }
        else
        {
            // Build from current request
            var scheme = ctx.Request.Scheme;
            var host = ctx.Request.Host; // may include port
            if (string.IsNullOrEmpty(host.Host))
                throw new ArgumentException("Request host is missing");
            var builder = new UriBuilder(scheme, host.Host, host.Port ?? (string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80));
            baseUri = builder.Uri;
        }

        string domain = baseUri.Host;
        if (!allowAny && !hosts.Contains(domain))
            throw new ArgumentException("Requested host is not allowlisted");

        return (domain, baseUri);
    }
}

public sealed class AuthChallenge
{
    public Guid Id { get; set; }
    public string Address { get; set; } = string.Empty;
    public long ChainId { get; set; }
    public string Nonce { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public string? UserAgent { get; set; }
    public string? Ip { get; set; }
}

public interface IAuthChallengeStore
{
    Task SaveAsync(AuthChallenge ch, CancellationToken ct = default);
    Task<AuthChallenge?> GetAsync(Guid id, CancellationToken ct = default);
    Task<bool> TryMarkUsedAsync(Guid id, CancellationToken ct = default);
}

public sealed class PostgresAuthChallengeStore : IAuthChallengeStore
{
    private readonly string _connString;
    private readonly ILogger<PostgresAuthChallengeStore> _log;

    public PostgresAuthChallengeStore(string connString, ILogger<PostgresAuthChallengeStore> log)
    {
        _connString = connString; _log = log; EnsureSchema();
    }

    private void EnsureSchema()
    {
        try
        {
            using var conn = new Npgsql.NpgsqlConnection(_connString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS auth_challenges (
  id uuid PRIMARY KEY,
  address text NOT NULL,
  chain_id bigint NOT NULL,
  nonce text NOT NULL,
  message text NOT NULL,
  issued_at timestamptz NOT NULL,
  expires_at timestamptz NOT NULL,
  used_at timestamptz NULL,
  user_agent text NULL,
  ip text NULL
);";
            cmd.ExecuteNonQuery();

            using var ix = conn.CreateCommand();
            ix.CommandText = "CREATE INDEX IF NOT EXISTS ix_auth_addr_nonce ON auth_challenges (address, nonce);";
            ix.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to ensure auth_challenges schema");
            throw;
        }
    }

    public async Task SaveAsync(AuthChallenge ch, CancellationToken ct = default)
    {
        using var conn = new Npgsql.NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO auth_challenges (id, address, chain_id, nonce, message, issued_at, expires_at, user_agent, ip)
VALUES (@id, @addr, @chain, @nonce, @msg, @iat, @exp, @ua, @ip)";
        cmd.Parameters.AddWithValue("@id", ch.Id);
        cmd.Parameters.AddWithValue("@addr", ch.Address.ToLowerInvariant());
        cmd.Parameters.AddWithValue("@chain", ch.ChainId);
        cmd.Parameters.AddWithValue("@nonce", ch.Nonce);
        cmd.Parameters.AddWithValue("@msg", ch.Message);
        cmd.Parameters.AddWithValue("@iat", ch.IssuedAt);
        cmd.Parameters.AddWithValue("@exp", ch.ExpiresAt);
        cmd.Parameters.AddWithValue("@ua", (object?)ch.UserAgent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ip", (object?)ch.Ip ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<AuthChallenge?> GetAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = new Npgsql.NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT address, chain_id, nonce, message, issued_at, expires_at, used_at, user_agent, ip FROM auth_challenges WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new AuthChallenge
        {
            Id = id,
            Address = reader.GetString(0),
            ChainId = reader.GetInt64(1),
            Nonce = reader.GetString(2),
            Message = reader.GetString(3),
            IssuedAt = reader.GetFieldValue<DateTimeOffset>(4),
            ExpiresAt = reader.GetFieldValue<DateTimeOffset>(5),
            UsedAt = reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset?>(6),
            UserAgent = reader.IsDBNull(7) ? null : reader.GetString(7),
            Ip = reader.IsDBNull(8) ? null : reader.GetString(8)
        };
    }

    public async Task<bool> TryMarkUsedAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = new Npgsql.NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE auth_challenges
SET used_at = now()
WHERE id = @id
  AND used_at IS NULL
  AND expires_at > now();";
        cmd.Parameters.AddWithValue("@id", id);
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        return rows == 1;
    }
}
