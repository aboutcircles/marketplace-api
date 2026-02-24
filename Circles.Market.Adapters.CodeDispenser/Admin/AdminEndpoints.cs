using Circles.Market.Adapters.CodeDispenser.Admin;
using Circles.Market.Shared.Admin;
using Microsoft.AspNetCore.Authorization;
using Npgsql;

namespace Circles.Market.Adapters.CodeDispenser.Admin;

public static class AdminEndpoints
{
    public static void MapCodeDispenserAdminApi(this WebApplication app, string adminBasePath, string postgresConn)
    {
        var group = app.MapGroup(adminBasePath).RequireAuthorization();

        group.MapGet("/health", () => Results.Json(new { ok = true }));

        group.MapGet("/code-pools", async (CancellationToken ct) =>
        {
            var result = new List<CodePoolEntry>();
            await using var conn = new NpgsqlConnection(postgresConn);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT pool_id, (SELECT COUNT(*) FROM code_pool_codes c WHERE c.pool_id = p.pool_id) AS remaining
FROM code_pools p
ORDER BY pool_id";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                result.Add(new CodePoolEntry
                {
                    PoolId = reader.GetString(0),
                    Remaining = reader.GetInt64(1)
                });
            }
            return Results.Json(result);
        });

        group.MapPost("/code-pools", async (CodePoolCreateRequest req, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.PoolId))
                return Results.BadRequest(new { error = "poolId is required" });
            var poolId = req.PoolId.Trim();

            await using var conn = new NpgsqlConnection(postgresConn);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO code_pools(pool_id) VALUES ($1) ON CONFLICT DO NOTHING";
            cmd.Parameters.AddWithValue(poolId);
            await cmd.ExecuteNonQueryAsync(ct);

            return Results.Json(new { poolId });
        });

        group.MapPost("/code-pools/{poolId}/seed", async (string poolId, CodePoolSeedRequest req, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(poolId))
                return Results.BadRequest(new { error = "poolId is required" });
            if (req.Codes == null || req.Codes.Count == 0)
                return Results.BadRequest(new { error = "codes must be non-empty" });

            string pool = poolId.Trim();
            var codes = req.Codes.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToList();
            if (codes.Count == 0)
                return Results.BadRequest(new { error = "codes must be non-empty" });

            await using var conn = new NpgsqlConnection(postgresConn);
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            await using (var ensure = conn.CreateCommand())
            {
                ensure.Transaction = tx;
                ensure.CommandText = "INSERT INTO code_pools(pool_id) VALUES ($1) ON CONFLICT DO NOTHING";
                ensure.Parameters.AddWithValue(pool);
                await ensure.ExecuteNonQueryAsync(ct);
            }

            await using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO code_pool_codes(pool_id, code) VALUES ($1, $2) ON CONFLICT DO NOTHING";
                foreach (var code in codes)
                {
                    ins.Parameters.Clear();
                    ins.Parameters.AddWithValue(pool);
                    ins.Parameters.AddWithValue(code);
                    await ins.ExecuteNonQueryAsync(ct);
                }
            }

            await tx.CommitAsync(ct);
            return Results.Json(new { poolId = pool, added = codes.Count });
        });

        group.MapGet("/mappings", async (CancellationToken ct) =>
        {
            var result = new List<CodeMappingDto>();
            await using var conn = new NpgsqlConnection(postgresConn);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT chain_id, seller_address, sku, pool_id, download_url_template, enabled, revoked_at
FROM code_mappings
ORDER BY chain_id, seller_address, sku";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                result.Add(new CodeMappingDto
                {
                    ChainId = reader.GetInt64(0),
                    Seller = reader.GetString(1),
                    Sku = reader.GetString(2),
                    PoolId = reader.GetString(3),
                    DownloadUrlTemplate = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Enabled = reader.GetBoolean(5),
                    RevokedAt = reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset?>(6)
                });
            }
            return Results.Json(result);
        });

        group.MapPut("/mappings", async (CodeMappingUpsertRequest req, CancellationToken ct) =>
        {
            if (req.ChainId <= 0) return Results.BadRequest(new { error = "chainId must be > 0" });
            if (string.IsNullOrWhiteSpace(req.Seller) || string.IsNullOrWhiteSpace(req.Sku) || string.IsNullOrWhiteSpace(req.PoolId))
                return Results.BadRequest(new { error = "seller, sku, poolId are required" });

            string seller = req.Seller.Trim().ToLowerInvariant();
            string sku = req.Sku.Trim().ToLowerInvariant();
            string pool = req.PoolId.Trim();
            string? tmpl = string.IsNullOrWhiteSpace(req.DownloadUrlTemplate) ? null : req.DownloadUrlTemplate.Trim();

            await using var conn = new NpgsqlConnection(postgresConn);
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            await using (var ensure = conn.CreateCommand())
            {
                ensure.Transaction = tx;
                ensure.CommandText = "INSERT INTO code_pools(pool_id) VALUES ($1) ON CONFLICT DO NOTHING";
                ensure.Parameters.AddWithValue(pool);
                await ensure.ExecuteNonQueryAsync(ct);
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO code_mappings (chain_id, seller_address, sku, pool_id, download_url_template, enabled, created_at, revoked_at)
VALUES ($1, $2, $3, $4, $5, $6, now(), CASE WHEN $6 THEN NULL ELSE now() END)
ON CONFLICT (chain_id, seller_address, sku) DO UPDATE SET
  pool_id = EXCLUDED.pool_id,
  download_url_template = EXCLUDED.download_url_template,
  enabled = EXCLUDED.enabled,
  revoked_at = CASE WHEN EXCLUDED.enabled THEN NULL ELSE now() END";
                cmd.Parameters.AddWithValue(req.ChainId);
                cmd.Parameters.AddWithValue(seller);
                cmd.Parameters.AddWithValue(sku);
                cmd.Parameters.AddWithValue(pool);
                cmd.Parameters.AddWithValue((object?)tmpl ?? DBNull.Value);
                cmd.Parameters.AddWithValue(req.Enabled);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);

            return Results.Json(new CodeMappingDto
            {
                ChainId = req.ChainId,
                Seller = seller,
                Sku = sku,
                PoolId = pool,
                DownloadUrlTemplate = tmpl,
                Enabled = req.Enabled,
                RevokedAt = req.Enabled ? null : DateTimeOffset.UtcNow
            });
        });

        group.MapDelete("/mappings/{chainId:long}/{seller}/{sku}", async (long chainId, string seller, string sku, CancellationToken ct) =>
        {
            if (chainId <= 0) return Results.BadRequest(new { error = "chainId must be > 0" });
            if (string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(sku))
                return Results.BadRequest(new { error = "seller and sku are required" });

            string sellerNorm = seller.Trim().ToLowerInvariant();
            string skuNorm = sku.Trim().ToLowerInvariant();

            await using var conn = new NpgsqlConnection(postgresConn);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"UPDATE code_mappings SET enabled=false, revoked_at=now()
WHERE chain_id=$1 AND seller_address=$2 AND sku=$3";
            cmd.Parameters.AddWithValue(chainId);
            cmd.Parameters.AddWithValue(sellerNorm);
            cmd.Parameters.AddWithValue(skuNorm);
            var rows = await cmd.ExecuteNonQueryAsync(ct);
            if (rows == 0)
                return Results.NotFound(new { error = "mapping not found" });
            return Results.Json(new { ok = true });
        });
    }
}
