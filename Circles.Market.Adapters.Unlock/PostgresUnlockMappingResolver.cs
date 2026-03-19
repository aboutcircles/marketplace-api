using Npgsql;

namespace Circles.Market.Adapters.Unlock;

public sealed class PostgresUnlockMappingResolver : IUnlockMappingResolver
{
    private readonly string _connString;
    private readonly ILogger<PostgresUnlockMappingResolver> _log;

    public PostgresUnlockMappingResolver(string connString, ILogger<PostgresUnlockMappingResolver> log)
    {
        _connString = connString ?? throw new ArgumentNullException(nameof(connString));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<(bool Mapped, UnlockMappingEntry? Entry)> TryResolveAsync(long chainId, string sellerAddress, string sku, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sellerAddress) || string.IsNullOrWhiteSpace(sku))
            return (false, null);

        string seller = sellerAddress.Trim().ToLowerInvariant();
        string skuNorm = sku.Trim().ToLowerInvariant();

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT chain_id, seller_address, sku, lock_address, rpc_url, service_private_key,
       duration_seconds, expiration_unix, key_manager_mode, fixed_key_manager,
       locksmith_base, max_supply, enabled, revoked_at
FROM unlock_mappings
WHERE chain_id = $1
  AND seller_address = $2
  AND sku = $3
  AND enabled = true
  AND revoked_at IS NULL
LIMIT 1";
        cmd.Parameters.AddWithValue(chainId);
        cmd.Parameters.AddWithValue(seller);
        cmd.Parameters.AddWithValue(skuNorm);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return (false, null);

        return (true, ReadEntry(reader));
    }

    public async Task<List<UnlockMappingEntry>> ListMappingsAsync(CancellationToken ct)
    {
        var result = new List<UnlockMappingEntry>();

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT chain_id, seller_address, sku, lock_address, rpc_url, service_private_key,
       duration_seconds, expiration_unix, key_manager_mode, fixed_key_manager,
       locksmith_base, max_supply, enabled, revoked_at
FROM unlock_mappings
ORDER BY chain_id, seller_address, sku";

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(ReadEntry(reader));
        }

        return result;
    }

    public async Task UpsertMappingAsync(UnlockMappingEntry entry, bool enabled, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO unlock_mappings(
  chain_id, seller_address, sku,
  lock_address, rpc_url, service_private_key,
  duration_seconds, expiration_unix,
  key_manager_mode, fixed_key_manager,
  locksmith_base,
  max_supply,
  enabled, created_at, revoked_at
)
VALUES (
  $1, $2, $3,
  $4, $5, $6,
  $7, $8,
  $9, $10,
  $11,
  $12,
  $13, now(), CASE WHEN $13 THEN NULL ELSE now() END
)
ON CONFLICT (chain_id, seller_address, sku) DO UPDATE SET
  lock_address = EXCLUDED.lock_address,
  rpc_url = EXCLUDED.rpc_url,
  service_private_key = EXCLUDED.service_private_key,
  duration_seconds = EXCLUDED.duration_seconds,
  expiration_unix = EXCLUDED.expiration_unix,
  key_manager_mode = EXCLUDED.key_manager_mode,
  fixed_key_manager = EXCLUDED.fixed_key_manager,
  locksmith_base = EXCLUDED.locksmith_base,
  max_supply = EXCLUDED.max_supply,
  enabled = EXCLUDED.enabled,
  revoked_at = CASE WHEN EXCLUDED.enabled THEN NULL ELSE now() END";

        cmd.Parameters.AddWithValue(entry.ChainId);
        cmd.Parameters.AddWithValue(entry.Seller.Trim().ToLowerInvariant());
        cmd.Parameters.AddWithValue(entry.Sku.Trim().ToLowerInvariant());
        cmd.Parameters.AddWithValue(entry.LockAddress.Trim());
        cmd.Parameters.AddWithValue(entry.RpcUrl.Trim());
        cmd.Parameters.AddWithValue(entry.ServicePrivateKey.Trim());
        cmd.Parameters.AddWithValue((object?)entry.DurationSeconds ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)entry.ExpirationUnix ?? DBNull.Value);
        cmd.Parameters.AddWithValue(entry.KeyManagerMode.Trim().ToLowerInvariant());
        cmd.Parameters.AddWithValue((object?)entry.FixedKeyManager ?? DBNull.Value);
        cmd.Parameters.AddWithValue(entry.LocksmithBase.Trim());
        cmd.Parameters.AddWithValue(entry.MaxSupply);
        cmd.Parameters.AddWithValue(enabled);

        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Failed to upsert unlock mapping chain={Chain} seller={Seller} sku={Sku}",
                entry.ChainId,
                entry.Seller,
                entry.Sku);
            throw;
        }
    }

    public async Task<bool> DisableMappingAsync(long chainId, string sellerAddress, string sku, CancellationToken ct)
    {
        string seller = sellerAddress.Trim().ToLowerInvariant();
        string skuNorm = sku.Trim().ToLowerInvariant();

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE unlock_mappings
SET enabled = false,
    revoked_at = now()
WHERE chain_id = $1
  AND seller_address = $2
  AND sku = $3";
        cmd.Parameters.AddWithValue(chainId);
        cmd.Parameters.AddWithValue(seller);
        cmd.Parameters.AddWithValue(skuNorm);

        int rows = await cmd.ExecuteNonQueryAsync(ct);
        return rows > 0;
    }

    private static UnlockMappingEntry ReadEntry(NpgsqlDataReader reader)
    {
        return new UnlockMappingEntry
        {
            ChainId = reader.GetInt64(0),
            Seller = reader.GetString(1),
            Sku = reader.GetString(2),
            LockAddress = reader.GetString(3),
            RpcUrl = reader.GetString(4),
            ServicePrivateKey = reader.GetString(5),
            DurationSeconds = reader.IsDBNull(6) ? null : reader.GetInt64(6),
            ExpirationUnix = reader.IsDBNull(7) ? null : reader.GetInt64(7),
            KeyManagerMode = reader.GetString(8),
            FixedKeyManager = reader.IsDBNull(9) ? null : reader.GetString(9),
            LocksmithBase = reader.GetString(10),
            MaxSupply = reader.GetInt64(11),
            Enabled = reader.GetBoolean(12),
            RevokedAt = reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset?>(13)
        };
    }
}
