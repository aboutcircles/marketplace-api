using Npgsql;

namespace Circles.Market.Adapters.Odoo.Db;

public interface IInventoryStockStore
{
    Task<long?> GetAvailableQtyAsync(long chainId, string sellerAddress, string sku, CancellationToken ct);
    Task SetAvailableQtyAsync(long chainId, string sellerAddress, string sku, long availableQty, string? updatedBy, CancellationToken ct);
    Task<bool> TryDecrementAsync(long chainId, string sellerAddress, string sku, long quantity, CancellationToken ct);
    Task IncrementAsync(long chainId, string sellerAddress, string sku, long quantity, CancellationToken ct);
}

public sealed class PostgresInventoryStockStore : IInventoryStockStore
{
    private readonly string _connString;
    private readonly ILogger<PostgresInventoryStockStore> _log;

    public PostgresInventoryStockStore(string connString, ILogger<PostgresInventoryStockStore> log)
    {
        _connString = connString ?? throw new ArgumentNullException(nameof(connString));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<long?> GetAvailableQtyAsync(long chainId, string sellerAddress, string sku, CancellationToken ct)
    {
        string seller = sellerAddress.Trim().ToLowerInvariant();
        string skuNorm = sku.Trim().ToLowerInvariant();

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT available_qty
FROM inventory_stock
WHERE chain_id=$1 AND seller_address=$2 AND sku=$3
LIMIT 1";
        cmd.Parameters.AddWithValue(chainId);
        cmd.Parameters.AddWithValue(seller);
        cmd.Parameters.AddWithValue(skuNorm);

        var scalar = await cmd.ExecuteScalarAsync(ct);
        if (scalar is null || scalar == DBNull.Value)
        {
            return null;
        }

        return Convert.ToInt64(scalar);
    }

    public async Task SetAvailableQtyAsync(long chainId, string sellerAddress, string sku, long availableQty, string? updatedBy, CancellationToken ct)
    {
        if (availableQty < 0) throw new ArgumentOutOfRangeException(nameof(availableQty));

        string seller = sellerAddress.Trim().ToLowerInvariant();
        string skuNorm = sku.Trim().ToLowerInvariant();

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO inventory_stock(chain_id, seller_address, sku, available_qty, updated_at, updated_by)
VALUES ($1, $2, $3, $4, now(), $5)
ON CONFLICT (chain_id, seller_address, sku)
DO UPDATE SET
  available_qty = EXCLUDED.available_qty,
  updated_at = now(),
  updated_by = EXCLUDED.updated_by";
        cmd.Parameters.AddWithValue(chainId);
        cmd.Parameters.AddWithValue(seller);
        cmd.Parameters.AddWithValue(skuNorm);
        cmd.Parameters.AddWithValue(availableQty);
        cmd.Parameters.AddWithValue((object?)updatedBy ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> TryDecrementAsync(long chainId, string sellerAddress, string sku, long quantity, CancellationToken ct)
    {
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));

        string seller = sellerAddress.Trim().ToLowerInvariant();
        string skuNorm = sku.Trim().ToLowerInvariant();

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE inventory_stock
SET available_qty = available_qty - $4,
    updated_at = now()
WHERE chain_id = $1 AND seller_address = $2 AND sku = $3
  AND available_qty >= $4";
        cmd.Parameters.AddWithValue(chainId);
        cmd.Parameters.AddWithValue(seller);
        cmd.Parameters.AddWithValue(skuNorm);
        cmd.Parameters.AddWithValue(quantity);

        int rows = await cmd.ExecuteNonQueryAsync(ct);
        return rows > 0;
    }

    public async Task IncrementAsync(long chainId, string sellerAddress, string sku, long quantity, CancellationToken ct)
    {
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));

        string seller = sellerAddress.Trim().ToLowerInvariant();
        string skuNorm = sku.Trim().ToLowerInvariant();

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE inventory_stock
SET available_qty = available_qty + $4,
    updated_at = now()
WHERE chain_id = $1 AND seller_address = $2 AND sku = $3";
        cmd.Parameters.AddWithValue(chainId);
        cmd.Parameters.AddWithValue(seller);
        cmd.Parameters.AddWithValue(skuNorm);
        cmd.Parameters.AddWithValue(quantity);

        int rows = await cmd.ExecuteNonQueryAsync(ct);
        if (rows == 0)
        {
            _log.LogWarning("IncrementAsync: stock row missing for seller={Seller} chain={Chain} sku={Sku}", seller, chainId, skuNorm);
        }
    }
}
