using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Circles.Market.Adapters.Odoo.Auth;

public sealed class PostgresTrustedCallerAuth : ITrustedCallerAuth
{
    private readonly string _connString;
    private readonly ILogger<PostgresTrustedCallerAuth> _log;

    public PostgresTrustedCallerAuth(string connString, ILogger<PostgresTrustedCallerAuth> log)
    {
        _connString = connString ?? throw new ArgumentNullException(nameof(connString));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<TrustedCallerAuthResult> AuthorizeAsync(string? rawApiKey, string requiredScope, long chainId, string seller, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawApiKey))
        {
            return new TrustedCallerAuthResult { Allowed = false, Reason = "missing api key" };
        }

        // compute SHA256 of raw key
        byte[] keyBytes = Encoding.UTF8.GetBytes(rawApiKey);
        byte[] hashBytes = SHA256.HashData(keyBytes);

        try
        {
            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT caller_id, scopes, seller_address, chain_id, enabled, revoked_at
FROM trusted_callers
WHERE api_key_sha256 = $1";
            cmd.Parameters.AddWithValue(hashBytes);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                _log.LogWarning("AuthorizeAsync: No trusted caller found for the provided API key (SHA256: {Hash})", 
                    Convert.ToHexString(hashBytes));
                return new TrustedCallerAuthResult { Allowed = false, Reason = "unknown key" };
            }

            string callerId = reader.GetString(0);
            var scopes = reader.GetFieldValue<string[]>(1);
            string? sellerAddress = reader.IsDBNull(2) ? null : reader.GetString(2)?.ToLowerInvariant();
            long? chain = reader.IsDBNull(3) ? null : reader.GetInt64(3);
            bool enabled = reader.GetBoolean(4);
            DateTimeOffset? revokedAt = reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5);

            if (!enabled || revokedAt != null)
            {
                _log.LogWarning("AuthorizeAsync: Trusted caller {CallerId} is disabled or revoked (Enabled={Enabled}, RevokedAt={RevokedAt})", 
                    callerId, enabled, revokedAt);
                return new TrustedCallerAuthResult { Allowed = false, Reason = "revoked/disabled" };
            }

            if (!scopes.Contains(requiredScope, StringComparer.OrdinalIgnoreCase))
            {
                _log.LogWarning("AuthorizeAsync: Trusted caller {CallerId} does not have required scope {RequiredScope}. Has: {Scopes}", 
                    callerId, requiredScope, string.Join(", ", scopes));
                return new TrustedCallerAuthResult { Allowed = false, Reason = "insufficient scope" };
            }

            string sellerNorm = seller.Trim().ToLowerInvariant();
            if (sellerAddress is not null && !string.Equals(sellerAddress, sellerNorm, StringComparison.Ordinal))
            {
                _log.LogWarning("AuthorizeAsync: Trusted caller {CallerId} seller mismatch. Expected: {Expected}, Actual: {Actual}", 
                    callerId, sellerAddress, sellerNorm);
                return new TrustedCallerAuthResult { Allowed = false, Reason = "seller mismatch" };
            }

            if (chain is not null && chain.Value != chainId)
            {
                _log.LogWarning("AuthorizeAsync: Trusted caller {CallerId} chain mismatch. Expected: {Expected}, Actual: {Actual}", 
                    callerId, chain, chainId);
                return new TrustedCallerAuthResult { Allowed = false, Reason = "chain mismatch" };
            }

            _log.LogInformation("AuthorizeAsync: Trusted caller {CallerId} authorized for scope {Scope}", callerId, requiredScope);
            return new TrustedCallerAuthResult { Allowed = true, CallerId = callerId };
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error during AuthorizeAsync for caller lookup");
            return new TrustedCallerAuthResult { Allowed = false, Reason = "internal error" };
        }
    }
}
