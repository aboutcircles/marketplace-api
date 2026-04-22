using Npgsql;

namespace Circles.Market.Adapters.WooCommerce.Db;

/// <summary>
/// Interface for local stock overrides (mirrors the Odoo adapter pattern).
/// </summary>
public interface IInventoryStockStore
{
    Task<int?> GetAvailableQtyAsync(long chainId, string sellerAddress, string sku, CancellationToken ct);
    Task SetStockAsync(long chainId, string sellerAddress, string sku, int quantity, CancellationToken ct);
    Task<bool> TryDecrementAsync(long chainId, string sellerAddress, string sku, int quantity, CancellationToken ct);
    Task IncrementAsync(long chainId, string sellerAddress, string sku, int quantity, CancellationToken ct);
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

    public async Task<int?> GetAvailableQtyAsync(long chainId, string sellerAddress, string sku, CancellationToken ct)
    {
        string sellerNorm = sellerAddress.Trim().ToLowerInvariant();
        string skuNorm = sku.Trim();

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);

        const string sql = """
            SELECT stock_quantity FROM wc_inventory_stock
            WHERE chain_id = @c AND seller_address = @s AND sku = @sku;
            """;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@c", chainId);
        cmd.Parameters.AddWithValue("@s", sellerNorm);
        cmd.Parameters.AddWithValue("@sku", skuNorm);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result == null || result == DBNull.Value ? null : (int?)result;
    }

    public async Task SetStockAsync(long chainId, string sellerAddress, string sku, int quantity, CancellationToken ct)
    {
        string sellerNorm = sellerAddress.Trim().ToLowerInvariant();
        string skuNorm = sku.Trim();

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);

        const string sql = """
            INSERT INTO wc_inventory_stock(id, chain_id, seller_address, sku, stock_quantity, updated_at)
            VALUES (gen_random_uuid(), @c, @s, @sku, @qty, now())
            ON CONFLICT (chain_id, seller_address, sku) DO UPDATE SET stock_quantity = @qty, updated_at = now();
            """;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@c", chainId);
        cmd.Parameters.AddWithValue("@s", sellerNorm);
        cmd.Parameters.AddWithValue("@sku", skuNorm);
        cmd.Parameters.AddWithValue("@qty", quantity);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> TryDecrementAsync(long chainId, string sellerAddress, string sku, int quantity, CancellationToken ct)
    {
        string sellerNorm = sellerAddress.Trim().ToLowerInvariant();
        string skuNorm = sku.Trim();

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);

        // Succeed for unlimited stock (-1) without decrementing; otherwise decrement if sufficient
        const string sql = """
            UPDATE wc_inventory_stock
            SET stock_quantity = CASE WHEN stock_quantity = -1 THEN -1 ELSE stock_quantity - @qty END,
                updated_at = now()
            WHERE chain_id = @c AND seller_address = @s AND sku = @sku
              AND (stock_quantity = -1 OR (stock_quantity >= 0 AND stock_quantity >= @qty))
            RETURNING id;
            """;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@c", chainId);
        cmd.Parameters.AddWithValue("@s", sellerNorm);
        cmd.Parameters.AddWithValue("@sku", skuNorm);
        cmd.Parameters.AddWithValue("@qty", quantity);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result != null;
    }

    public async Task IncrementAsync(long chainId, string sellerAddress, string sku, int quantity, CancellationToken ct)
    {
        string sellerNorm = sellerAddress.Trim().ToLowerInvariant();
        string skuNorm = sku.Trim();

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);

        const string sql = """
            UPDATE wc_inventory_stock
            SET stock_quantity = stock_quantity + @qty, updated_at = now()
            WHERE chain_id = @c AND seller_address = @s AND sku = @sku;
            """;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@c", chainId);
        cmd.Parameters.AddWithValue("@s", sellerNorm);
        cmd.Parameters.AddWithValue("@sku", skuNorm);
        cmd.Parameters.AddWithValue("@qty", quantity);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}