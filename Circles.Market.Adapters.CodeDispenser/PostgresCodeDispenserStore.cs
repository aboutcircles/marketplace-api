using Npgsql;
using System.Security.Cryptography;
using System.Text;

namespace Circles.Market.Adapters.CodeDispenser;

public interface ICodeDispenserStore
{
    Task EnsureSchemaAsync(CancellationToken ct);
    Task SeedFromDirAsync(string? poolsDir, CancellationToken ct);
    Task<(AssignmentStatus status, string? code)> AssignAsync(long chainId, string seller, string paymentReference, string orderId, string sku, string poolId, CancellationToken ct);
    Task<(AssignmentStatus status, List<string> codes)> AssignManyAsync(long chainId, string seller, string paymentReference, string orderId, string sku, string poolId, int quantity, CancellationToken ct);
    Task<long> GetRemainingAsync(string poolId, CancellationToken ct);
}

public sealed class PostgresCodeDispenserStore : ICodeDispenserStore
{
    private static long ComputeAdvisoryKey(long chainId, string seller, string paymentReference)
    {
        // Deterministic 64-bit signed key from inputs. Use SHA256 and take first 8 bytes little-endian.
        var sellerNorm = (seller ?? string.Empty).Trim().ToLowerInvariant();
        var payNorm = paymentReference ?? string.Empty;
        var input = Encoding.UTF8.GetBytes($"{chainId}|{sellerNorm}|{payNorm}");
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        // Convert first 8 bytes to Int64
        long key = BitConverter.ToInt64(hash[..8]);
        // Avoid zero which is still valid but keep as-is; Postgres accepts any bigint.
        return key;
    }
    private readonly string _connString;
    private readonly ILogger<PostgresCodeDispenserStore> _log;

    public PostgresCodeDispenserStore(string connString, ILogger<PostgresCodeDispenserStore> log)
    {
        _connString = connString ?? throw new ArgumentNullException(nameof(connString));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        _log.LogInformation("EnsureSchemaAsync starting for CodeDispenser tables...");
        try
        {
            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);
            _log.LogInformation("Postgres connection opened for CodeDispenser schema creation.");
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS code_pools (
  pool_id text PRIMARY KEY,
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS code_pool_codes (
  id bigserial PRIMARY KEY,
  pool_id text NOT NULL REFERENCES code_pools(pool_id),
  code text NOT NULL,
  inserted_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE(pool_id, code)
);

CREATE TABLE IF NOT EXISTS code_assignments (
  chain_id bigint NOT NULL,
  seller_address text NOT NULL,
  payment_reference text NOT NULL,
  order_id text NOT NULL,
  pool_id text NOT NULL,
  sku text NOT NULL,
  code text NOT NULL,
  assigned_at timestamptz NOT NULL DEFAULT now(),
  seq integer NOT NULL,
  PRIMARY KEY(chain_id, seller_address, payment_reference, seq)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_code_assignments_pool_code ON code_assignments(pool_id, code);

CREATE TABLE IF NOT EXISTS trusted_callers (
  caller_id text PRIMARY KEY,
  api_key_sha256 bytea NOT NULL UNIQUE,
  scopes text[] NOT NULL,
  seller_address text NULL,
  chain_id bigint NULL,
  enabled boolean NOT NULL DEFAULT true,
  created_at timestamptz NOT NULL DEFAULT now(),
  revoked_at timestamptz NULL
);

CREATE INDEX IF NOT EXISTS ix_trusted_callers_enabled ON trusted_callers(enabled);

-- New: mapping table for seller+sku -> pool
CREATE TABLE IF NOT EXISTS code_mappings (
  chain_id              bigint NOT NULL,
  seller_address        text NOT NULL,
  sku                   text NOT NULL,
  pool_id               text NOT NULL REFERENCES code_pools(pool_id),
  download_url_template text NULL,

  enabled               boolean NOT NULL DEFAULT true,
  created_at            timestamptz NOT NULL DEFAULT now(),
  revoked_at            timestamptz NULL,

  PRIMARY KEY (chain_id, seller_address, sku)
);

CREATE INDEX IF NOT EXISTS ix_code_mappings_enabled ON code_mappings(enabled);
";
            await cmd.ExecuteNonQueryAsync(ct);

            _log.LogInformation("Base tables created. Verifying schema details...");

            // Verify schema deterministically (no best-effort migrations). Fail fast on mismatch.
            await using (var verify = conn.CreateCommand())
            {
                verify.CommandText = @"SELECT data_type FROM information_schema.columns WHERE table_name='code_assignments' AND column_name='seq'";
                var typeObj = await verify.ExecuteScalarAsync(ct);
                var type = typeObj?.ToString();
                if (!string.Equals(type, "integer", StringComparison.OrdinalIgnoreCase))
                {
                    throw new Exception("Schema mismatch: code_assignments.seq must be integer. Drop and recreate DB or migrate schema.");
                }
            }
            await using (var verifyPk = conn.CreateCommand())
            {
                verifyPk.CommandText = @"SELECT array_agg(kcu.column_name ORDER BY kcu.ordinal_position)
FROM information_schema.table_constraints tc
JOIN information_schema.key_column_usage kcu ON tc.constraint_name = kcu.constraint_name
WHERE tc.table_name='code_assignments' AND tc.constraint_type='PRIMARY KEY'
GROUP BY tc.constraint_name";
                await using var reader = await verifyPk.ExecuteReaderAsync(ct);
                bool ok = false;
                while (await reader.ReadAsync(ct))
                {
                    if (reader.GetFieldValue<string[]>(0) is { } cols)
                    {
                        if (cols.SequenceEqual(new[] { "chain_id", "seller_address", "payment_reference", "seq" }))
                        {
                            ok = true; break;
                        }
                    }
                }
                if (!ok)
                    throw new Exception("Schema mismatch: code_assignments primary key must be (chain_id, seller_address, payment_reference, seq). Drop and recreate DB or migrate schema.");
            }

            // Verify code_mappings columns and PK
            await using (var verMapCols = conn.CreateCommand())
            {
                verMapCols.CommandText = @"SELECT column_name, data_type FROM information_schema.columns WHERE table_name='code_mappings' ORDER BY ordinal_position";
                var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["chain_id"] = "bigint",
                    ["seller_address"] = "text",
                    ["sku"] = "text",
                    ["pool_id"] = "text",
                    ["download_url_template"] = "text",
                    ["enabled"] = "boolean",
                    ["created_at"] = "timestamp with time zone",
                    ["revoked_at"] = "timestamp with time zone"
                };
                await using var rdr = await verMapCols.ExecuteReaderAsync(ct);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (await rdr.ReadAsync(ct))
                {
                    var col = rdr.GetString(0);
                    var type = rdr.GetString(1);
                    if (expected.TryGetValue(col, out var expType))
                    {
                        if (!string.Equals(type, expType, StringComparison.OrdinalIgnoreCase))
                            throw new Exception($"Schema mismatch: code_mappings.{col} must be {expType} but is {type}.");
                        seen.Add(col);
                    }
                }
                foreach (var kv in expected)
                {
                    if (!seen.Contains(kv.Key))
                        throw new Exception($"Schema mismatch: code_mappings.{kv.Key} is missing.");
                }
            }
            await using (var verMapPk = conn.CreateCommand())
            {
                verMapPk.CommandText = @"SELECT array_agg(kcu.column_name ORDER BY kcu.ordinal_position)
FROM information_schema.table_constraints tc
JOIN information_schema.key_column_usage kcu ON tc.constraint_name = kcu.constraint_name
WHERE tc.table_name='code_mappings' AND tc.constraint_type='PRIMARY KEY'
GROUP BY tc.constraint_name";
                await using var rdr2 = await verMapPk.ExecuteReaderAsync(ct);
                bool ok = false;
                while (await rdr2.ReadAsync(ct))
                {
                    if (rdr2.GetFieldValue<string[]>(0) is { } cols)
                    {
                        if (cols.SequenceEqual(new[] { "chain_id", "seller_address", "sku" }))
                        { ok = true; break; }
                    }
                }
                if (!ok) throw new Exception("Schema mismatch: code_mappings PK must be (chain_id, seller_address, sku).");
            }
            await using (var verFk = conn.CreateCommand())
            {
                verFk.CommandText = @"SELECT COUNT(1) FROM information_schema.table_constraints tc
JOIN information_schema.key_column_usage kcu ON tc.constraint_name = kcu.constraint_name
JOIN information_schema.constraint_column_usage ccu ON ccu.constraint_name = tc.constraint_name
WHERE tc.table_name='code_mappings' AND tc.constraint_type='FOREIGN KEY' AND ccu.table_name='code_pools' AND ccu.column_name='pool_id'";
                var count = (long)(await verFk.ExecuteScalarAsync(ct) ?? 0L);
                if (count <= 0) throw new Exception("Schema mismatch: code_mappings.pool_id must reference code_pools(pool_id).");
            }
            _log.LogInformation("EnsureSchemaAsync completed successfully for CodeDispenser.");
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "FATAL: EnsureSchemaAsync failed for CodeDispenser.");
            throw;
        }
    }

    public async Task SeedFromDirAsync(string? poolsDir, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(poolsDir)) return;
        if (!Directory.Exists(poolsDir))
        {
            _log.LogWarning("Pools directory {Dir} does not exist; skipping seeding.", poolsDir);
            return;
        }

        var files = Directory.GetFiles(poolsDir, "*.txt");
        foreach (var file in files)
        {
            var poolId = Path.GetFileNameWithoutExtension(file);
            await EnsurePoolExistsAsync(poolId, ct);

            var codes = await File.ReadAllLinesAsync(file, ct);
            // Best-effort bulk insert with ON CONFLICT DO NOTHING
            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            await using (var ins = conn.CreateCommand())
            {
                ins.CommandText = "INSERT INTO code_pool_codes(pool_id, code) VALUES ($1, $2) ON CONFLICT DO NOTHING";
                foreach (var raw in codes)
                {
                    var code = raw.Trim();
                    if (string.IsNullOrWhiteSpace(code)) continue;
                    ins.Parameters.Clear();
                    ins.Parameters.AddWithValue(poolId);
                    ins.Parameters.AddWithValue(code);
                    await ins.ExecuteNonQueryAsync(ct);
                }
            }

            await tx.CommitAsync(ct);
            _log.LogInformation("Seeded pool {PoolId} with codes from {File}", poolId, file);
        }
    }

    private async Task EnsurePoolExistsAsync(string poolId, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO code_pools(pool_id) VALUES ($1) ON CONFLICT DO NOTHING";
        cmd.Parameters.AddWithValue(poolId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<(AssignmentStatus status, string? code)> AssignAsync(long chainId, string seller, string paymentReference, string orderId, string sku, string poolId, CancellationToken ct)
    {
        var (status, codes) = await AssignManyAsync(chainId, seller, paymentReference, orderId, sku, poolId, 1, ct);
        return (status, codes.FirstOrDefault());
    }

    public async Task<(AssignmentStatus status, List<string> codes)> AssignManyAsync(long chainId, string seller, string paymentReference, string orderId, string sku, string poolId, int quantity, CancellationToken ct)
    {
        if (quantity <= 0) quantity = 1;
        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Acquire transaction-scoped advisory lock to serialize assignments per (chainId,seller,paymentReference)
        long advKey = ComputeAdvisoryKey(chainId, seller, paymentReference);
        await using (var lockCmd = conn.CreateCommand())
        {
            lockCmd.Transaction = tx;
            lockCmd.CommandText = "SELECT pg_advisory_xact_lock($1)";
            lockCmd.Parameters.AddWithValue(advKey);
            await lockCmd.ExecuteNonQueryAsync(ct);
        }

        // 1) Idempotency check for existing assignments for this key+pool+sku
        var existingCodes = new List<string>();
        int nextSeq = 0;
        await using (var check = conn.CreateCommand())
        {
            check.Transaction = tx;
            check.CommandText = @"SELECT code, seq FROM code_assignments
                                   WHERE chain_id=$1 AND seller_address=$2 AND payment_reference=$3 AND pool_id=$4 AND sku=$5
                                   ORDER BY seq ASC";
            check.Parameters.AddWithValue(chainId);
            check.Parameters.AddWithValue(seller);
            check.Parameters.AddWithValue(paymentReference);
            check.Parameters.AddWithValue(poolId);
            check.Parameters.AddWithValue(sku);
            await using var reader = await check.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                existingCodes.Add(reader.GetString(0));
                nextSeq = reader.GetInt32(1) + 1;
            }
        }
        if (existingCodes.Count == quantity)
        {
            await tx.CommitAsync(ct);
            return (AssignmentStatus.Ok, existingCodes);
        }
        if (existingCodes.Count > quantity)
        {
            await tx.CommitAsync(ct);
            return (AssignmentStatus.Error, existingCodes);
        }

        int missing = quantity - existingCodes.Count;
        // 2) Reserve exactly 'missing' codes atomically
        var reserved = new List<(long id, string code)>();
        await using (var reserve = conn.CreateCommand())
        {
            reserve.Transaction = tx;
            reserve.CommandText = "SELECT id, code FROM code_pool_codes WHERE pool_id=$1 ORDER BY id ASC LIMIT $2 FOR UPDATE";
            reserve.Parameters.AddWithValue(poolId);
            reserve.Parameters.AddWithValue(missing);
            await using var r = await reserve.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                reserved.Add((r.GetInt64(0), r.GetString(1)));
            }
        }
        if (reserved.Count < missing)
        {
            await tx.RollbackAsync(ct);
            return (AssignmentStatus.Depleted, new List<string>());
        }

        // 3) Insert assignments for reserved codes with increasing seq
        var allCodes = new List<string>(quantity);
        allCodes.AddRange(existingCodes);
        foreach (var rc in reserved)
        {
            await using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = @"INSERT INTO code_assignments(chain_id, seller_address, payment_reference, order_id, pool_id, sku, code, seq)
VALUES ($1,$2,$3,$4,$5,$6,$7,$8)";
                ins.Parameters.AddWithValue(chainId);
                ins.Parameters.AddWithValue(seller);
                ins.Parameters.AddWithValue(paymentReference);
                ins.Parameters.AddWithValue(orderId);
                ins.Parameters.AddWithValue(poolId);
                ins.Parameters.AddWithValue(sku);
                ins.Parameters.AddWithValue(rc.code);
                ins.Parameters.AddWithValue(nextSeq);
                await ins.ExecuteNonQueryAsync(ct);
            }
            nextSeq++;
            allCodes.Add(rc.code);
        }

        // 4) Delete reserved codes from pool by id
        await using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM code_pool_codes WHERE id = ANY($1)";
            del.Parameters.AddWithValue(reserved.Select(t => t.id).ToArray());
            await del.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return (AssignmentStatus.Ok, allCodes);
    }

    public async Task<long> GetRemainingAsync(string poolId, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM code_pool_codes WHERE pool_id=$1";
        cmd.Parameters.AddWithValue(poolId);
        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is long l) return l;
        if (result is int i) return i;
        return 0L;
    }
}
