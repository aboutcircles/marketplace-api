using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Circles.Market.Adapters.CodeDispenser.Auth;

public sealed class EnvTrustedCallerAuth : ITrustedCallerAuth
{
    private readonly ILogger<EnvTrustedCallerAuth> _log;
    private readonly string _sharedSecret;

    public EnvTrustedCallerAuth(ILogger<EnvTrustedCallerAuth> log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));

        var raw = Environment.GetEnvironmentVariable("CIRCLES_SERVICE_KEY");
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException("Missing required CIRCLES_SERVICE_KEY environment variable.");
        }

        _sharedSecret = raw.Trim();
        if (_sharedSecret.Length < 16)
        {
            _log.LogWarning("CIRCLES_SERVICE_KEY is unusually short. Consider using at least 16+ random bytes.");
        }
    }

    public Task<TrustedCallerAuthResult> AuthorizeAsync(
        string? rawApiKey,
        string requiredScope,
        long chainId,
        string seller,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawApiKey))
        {
            return Task.FromResult(new TrustedCallerAuthResult { Allowed = false, Reason = "missing api key" });
        }

        bool ok = FixedTimeEquals(rawApiKey.Trim(), _sharedSecret);
        if (!ok)
        {
            _log.LogWarning("AuthorizeAsync denied: invalid shared secret (scope={Scope} chain={Chain} seller={Seller})",
                requiredScope, chainId, seller);
            return Task.FromResult(new TrustedCallerAuthResult { Allowed = false, Reason = "invalid api key" });
        }

        return Task.FromResult(new TrustedCallerAuthResult { Allowed = true, CallerId = "env" });
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }
}
