using Npgsql;

namespace Circles.Market.Adapters.Odoo;

public sealed class PostgresInventoryMappingResolver : IInventoryMappingResolver
{
    private readonly string _connString;
    private readonly ILogger<PostgresInventoryMappingResolver> _log;

    public PostgresInventoryMappingResolver(string connString, ILogger<PostgresInventoryMappingResolver> log)
    {
        _connString = connString ?? throw new ArgumentNullException(nameof(connString));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<(bool Mapped, InventoryMappingEntry? Entry)> TryResolveAsync(long chainId, string sellerAddress, string sku, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sellerAddress) || string.IsNullOrWhiteSpace(sku))
        {
            return (false, null);
        }

        string sellerNorm = sellerAddress.Trim().ToLowerInvariant();
        string skuNorm = sku.Trim().ToLowerInvariant();

        try
        {
            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT seller_address, sku, odoo_product_code
FROM inventory_mappings
WHERE seller_address = $1 AND chain_id = $2 AND sku = $3 AND enabled = true AND revoked_at IS NULL
LIMIT 1";
            cmd.Parameters.AddWithValue(sellerNorm);
            cmd.Parameters.AddWithValue(chainId);
            cmd.Parameters.AddWithValue(skuNorm);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return (false, null);
            }

            var entry = new InventoryMappingEntry
            {
                Seller = reader.GetString(0),
                Sku = reader.GetString(1),
                OdooProductCode = reader.GetString(2)
            };

            return (true, entry);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error resolving inventory mapping for seller={Seller} sku={Sku}", sellerNorm, skuNorm);
            throw;
        }
    }
}
