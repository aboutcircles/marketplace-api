using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Circles.Market.Adapters.CodeDispenser.Auth;

public sealed class PostgresTrustedCallerAuth : ITrustedCallerAuth
{
    private readonly string _connString;
    private readonly ILogger<PostgresTrustedCallerAuth> _log;

    public PostgresTrustedCallerAuth(string connString, ILogger<PostgresTrustedCallerAuth> log)
    {
        _connString = connString;
        _log = log;
    }

    public async Task<TrustedCallerAuthResult> AuthorizeAsync(string? rawApiKey, string requiredScope, long chainId, string seller, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawApiKey))
        {
            return new TrustedCallerAuthResult { Allowed = false, Reason = "missing api key" };
        }
        // compute SHA256 of raw key
        byte[] keyBytes = Encoding.UTF8.GetBytes(rawApiKey);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(keyBytes, hash);
        var hashBytes = hash.ToArray();

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT caller_id, scopes, seller_address, chain_id, enabled, revoked_at
FROM trusted_callers
WHERE api_key_sha256 = $1";
        cmd.Parameters.AddWithValue(hashBytes);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
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
            return new TrustedCallerAuthResult { Allowed = false, Reason = "revoked/disabled" };
        }
        if (!scopes.Contains(requiredScope))
        {
            return new TrustedCallerAuthResult { Allowed = false, Reason = "insufficient scope" };
        }
        string sellerNorm = seller.Trim().ToLowerInvariant();
        if (sellerAddress is not null && !string.Equals(sellerAddress, sellerNorm, StringComparison.Ordinal))
        {
            return new TrustedCallerAuthResult { Allowed = false, Reason = "seller mismatch" };
        }
        if (chain is not null && chain.Value != chainId)
        {
            return new TrustedCallerAuthResult { Allowed = false, Reason = "chain mismatch" };
        }
        return new TrustedCallerAuthResult { Allowed = true, CallerId = callerId };
    }
}