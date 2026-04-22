using Npgsql;

namespace Circles.Market.Adapters.WooCommerce;

/// <summary>
/// Resolves SKU → WooCommerce product mappings from Postgres.
/// Mirrors <c>PostgresInventoryMappingResolver</c>.
/// </summary>
public interface IProductMappingResolver
{
    /// <summary>Resolves a SKU to WC product details.</summary>
    Task<ProductMappingInfo?> ResolveAsync(long chainId, string seller, string sku, CancellationToken ct);

    /// <summary>Lists all enabled mappings for a seller.</summary>
    Task<List<ProductMappingInfo>> ListAsync(long chainId, string seller, CancellationToken ct);
}

public sealed class PostgresProductMappingResolver : IProductMappingResolver
{
    private readonly string _connString;
    private readonly ILogger<PostgresProductMappingResolver> _log;

    public PostgresProductMappingResolver(string connString, ILogger<PostgresProductMappingResolver> log)
    {
        _connString = connString ?? throw new ArgumentNullException(nameof(connString));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<ProductMappingInfo?> ResolveAsync(long chainId, string seller, string sku, CancellationToken ct)
    {
        string sellerNorm = seller.Trim().ToLowerInvariant();
        string skuNorm = sku.Trim();

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);

        const string sql = """
            SELECT sku, wc_product_sku, wc_product_id
            FROM wc_product_mappings
            WHERE chain_id = @c AND seller_address = @s AND sku = @sku
              AND enabled = true AND revoked_at IS NULL
            LIMIT 1;
            """;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@c", chainId);
        cmd.Parameters.AddWithValue("@s", sellerNorm);
        cmd.Parameters.AddWithValue("@sku", skuNorm);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            _log.LogDebug("No product mapping for sku={Sku} seller={Seller} chain={Chain}", sku, seller, chainId);
            return null;
        }

        return new ProductMappingInfo
        {
            Sku = reader.GetString(0),
            WcProductSku = reader.GetString(1),
            WcProductId = reader.IsDBNull(2) ? null : reader.GetInt32(2)
        };
    }

    public async Task<List<ProductMappingInfo>> ListAsync(long chainId, string seller, CancellationToken ct)
    {
        string sellerNorm = seller.Trim().ToLowerInvariant();

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);

        const string sql = """
            SELECT sku, wc_product_sku, wc_product_id
            FROM wc_product_mappings
            WHERE chain_id = @c AND seller_address = @s
              AND enabled = true AND revoked_at IS NULL
            ORDER BY sku;
            """;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@c", chainId);
        cmd.Parameters.AddWithValue("@s", sellerNorm);

        var results = new List<ProductMappingInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new ProductMappingInfo
            {
                Sku = reader.GetString(0),
                WcProductSku = reader.GetString(1),
                WcProductId = reader.IsDBNull(2) ? null : reader.GetInt32(2)
            });
        }
        return results;
    }
}