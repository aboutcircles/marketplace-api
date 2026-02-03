using System.Text;
using Circles.Profiles.Interfaces;
using Circles.Profiles.Models.Core;
using Circles.Profiles.Sdk.Utils;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Nethereum.Hex.HexConvertors.Extensions;
using Nethereum.Signer;

namespace Circles.Market.Auth.Siwe;

public sealed class SiweAuthService
{
    private readonly SiweAuthOptions _options;
    private readonly IAuthChallengeStore _store;
    private readonly ISafeBytesVerifier _safeVerifier;
    private readonly ITokenService _tokens;
    private readonly ILogger _log;
    private readonly Func<string, string> _addressNormalizer;
    private readonly Func<string, bool> _addressValidator;

    public SiweAuthService(
        SiweAuthOptions options,
        IAuthChallengeStore store,
        ISafeBytesVerifier safeVerifier,
        ITokenService tokens,
        ILoggerFactory loggerFactory,
        Func<string, string>? addressNormalizer = null,
        Func<string, bool>? addressValidator = null)
    {
        _options = options;
        _store = store;
        _safeVerifier = safeVerifier;
        _tokens = tokens;
        _log = loggerFactory.CreateLogger("SiweAuth");
        _addressNormalizer = addressNormalizer ?? AddressUtils.NormalizeToLowercase;
        _addressValidator = addressValidator ?? AddressUtils.IsValidLowercaseAddress;
    }

    public async Task<ChallengeResponse> CreateChallengeAsync(HttpContext ctx, ChallengeRequest req, string defaultStatement)
    {
        string addr = _addressNormalizer(req.Address);
        long chainId = req.ChainId;
        if (chainId <= 0)
            throw new ArgumentException("chainId must be > 0");

        if (!_addressValidator(addr))
            throw new ArgumentException("address must be a 0x lowercase hex address");

        var ttlMinutes = Math.Clamp(req.ExpirationMinutes ?? (int)_options.DefaultChallengeTtl.TotalMinutes,
            _options.MinChallengeMinutes, _options.MaxChallengeMinutes);
        var ttl = TimeSpan.FromMinutes(ttlMinutes);

        var (domain, baseUri) = SiweAuthConfig.ResolveBaseUriAndValidate(ctx, _options);
        string uri = baseUri.GetLeftPart(UriPartial.Authority);
        string statement = string.IsNullOrWhiteSpace(req.Statement) ? defaultStatement : req.Statement!;

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

        await _store.SaveAsync(ch, ctx.RequestAborted);

        return new ChallengeResponse
        {
            ChallengeId = ch.Id,
            Message = message,
            Nonce = nonce,
            ExpiresAt = expiresAt
        };
    }

    public async Task<VerifyResponse> VerifyAsync(VerifyRequest req, CancellationToken ct)
    {
        var ch = await _store.GetAsync(req.ChallengeId, ct);
        if (ch is null)
            throw new UnauthorizedAccessException("challenge not found");

        if (ch.UsedAt is not null || ch.ExpiresAt < DateTimeOffset.UtcNow)
            throw new UnauthorizedAccessException("challenge expired or used");

        if (string.IsNullOrWhiteSpace(req.Signature) || !req.Signature.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("signature must be hex");

        byte[] sigBytes;
        try
        {
            sigBytes = req.Signature.HexToByteArray();
        }
        catch
        {
            throw new UnauthorizedAccessException("signature malformed");
        }

        // Strict shape: 65-byte signature required
        if (sigBytes.Length != 65)
            throw new UnauthorizedAccessException("signature malformed");

        // Optional: v must be in {0,1,27,28}
        byte v = sigBytes[64];
        bool vOk = v == 0 || v == 1 || v == 27 || v == 28;
        if (!vOk)
            throw new UnauthorizedAccessException("signature malformed");

        // Verify signature (EOA first, then 1271 bytes)
        if (!await VerifySignatureAsync(ch, req.Signature, sigBytes, ct))
            throw new UnauthorizedAccessException("signature invalid");

        // If allowlist is required, enforce it before consuming the challenge
        if (_options.RequireAllowlist)
        {
            var allowlist = SiweAuthConfig.LoadAllowlist(_options);
            if (allowlist.Count == 0)
                throw new InvalidOperationException("allowlist must include at least one address");

            var normalized = _addressNormalizer(ch.Address);
            if (!allowlist.Contains(normalized))
                throw new UnauthorizedAccessException("address not allowlisted");
        }

        // Atomic mark-used: only first successful verification can mint a token
        var marked = await _store.TryMarkUsedAsync(ch.Id, ct);
        if (!marked)
            throw new UnauthorizedAccessException("challenge already used");

        var token = _tokens.Issue(new TokenSubject(ch.Address, ch.ChainId), _options.TokenLifetime);
        return new VerifyResponse
        {
            Token = token,
            Address = ch.Address,
            ChainId = ch.ChainId,
            ExpiresIn = (int)_options.TokenLifetime.TotalSeconds
        };
    }

    private async Task<bool> VerifySignatureAsync(AuthChallenge ch, string signatureHex, byte[] sigBytes, CancellationToken ct)
    {
        string message = ch.Message;
        byte[] messageBytes = Encoding.UTF8.GetBytes(message);

        // EOA verification: EIP-191 personal_sign over the SIWE message.
        bool eoaOk;
        try
        {
            var signer = new EthereumMessageSigner();
            string recovered = signer.EncodeUTF8AndEcRecover(message, signatureHex);
            eoaOk = string.Equals(_addressNormalizer(recovered), _addressNormalizer(ch.Address), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            eoaOk = false;
        }

        if (eoaOk) return true;

        bool ok;
        try
        {
            ok = await _safeVerifier.Verify1271WithBytesAsync(messageBytes, ch.Address, sigBytes, ct);
        }
        catch (HttpRequestException ex)
        {
            _log.LogError(ex, "Verification transport error (RPC)");
            throw;
        }
        catch (IOException ex)
        {
            _log.LogError(ex, "Verification I/O error");
            throw;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _log.LogError(ex, "Verification timeout");
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Verification internal error");
            throw;
        }

        return ok;
    }
}
