using Npgsql;

namespace Circles.Market.Api.Inventory;

/// <summary>
/// Postgres implementation of IOneOffSalesStore for tracking one-off sales.
/// </summary>
public class PostgresOneOffSalesStore : IOneOffSalesStore
{
    private readonly string _connString;
    private readonly ILogger<PostgresOneOffSalesStore> _logger;

    public PostgresOneOffSalesStore(string connString, ILogger<PostgresOneOffSalesStore> logger)
    {
        _connString = connString;
        _logger = logger;
    }

    public async Task<bool> IsSoldAsync(long chainId, string seller, string sku, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(sku))
        {
            return false;
        }

        try
        {
            using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);
            
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT 1
FROM one_off_sales
WHERE chain_id = @chainId 
  AND seller_address = @seller 
  AND sku = @sku
LIMIT 1";
            
            cmd.Parameters.AddWithValue("@chainId", chainId);
            cmd.Parameters.AddWithValue("@seller", seller.ToLowerInvariant());
            cmd.Parameters.AddWithValue("@sku", sku.ToLowerInvariant());
            
            using var reader = await cmd.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct); // Returns true if a row exists
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check if one-off item is sold: chainId={ChainId}, seller={Seller}, sku={Sku}", chainId, seller, sku);
            throw;
        }
    }
}
