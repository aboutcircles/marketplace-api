using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;

namespace Circles.Market.Adapters.CodeDispenser;

public sealed class PostgresCodeMappingResolver : ICodeMappingResolver
{
    private readonly string _connString;
    private readonly IMemoryCache? _cache;

    private static readonly Regex HexAddress = new Regex("^0x[0-9a-f]{40}$", RegexOptions.Compiled);

    public PostgresCodeMappingResolver(string connString, IMemoryCache? cache = null)
    {
        _connString = connString ?? throw new ArgumentNullException(nameof(connString));
        _cache = cache;
    }

    public static (string seller, string sku) Normalize(string sellerAddress, string sku)
    {
        if (string.IsNullOrWhiteSpace(sellerAddress)) throw new ArgumentException("seller is required", nameof(sellerAddress));
        if (string.IsNullOrWhiteSpace(sku)) throw new ArgumentException("sku is required", nameof(sku));
        var seller = sellerAddress.Trim().ToLowerInvariant();
        var skuNorm = sku.Trim().ToLowerInvariant();
        if (!HexAddress.IsMatch(seller)) throw new ArgumentException("Invalid seller address format", nameof(sellerAddress));
        return (seller, skuNorm);
    }

    public bool TryResolve(long chainId, string sellerAddress, string sku, out CodeMappingEntry? entry)
    {
        entry = null;

        try
        {
            var (seller, skuNorm) = Normalize(sellerAddress, sku);
            var cacheKey = ($"map:{chainId}:{seller}", skuNorm);

            if (_cache != null && _cache.TryGetValue(cacheKey, out CodeMappingEntry? cached) && cached != null)
            {
                entry = cached;
                return true;
            }

            using var conn = new NpgsqlConnection(_connString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT pool_id, download_url_template
FROM code_mappings
WHERE chain_id=$1 AND seller_address=$2 AND sku=$3 AND enabled=true AND revoked_at IS NULL
LIMIT 1";
            cmd.Parameters.AddWithValue(chainId);
            cmd.Parameters.AddWithValue(seller);
            cmd.Parameters.AddWithValue(skuNorm);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return false;
            }

            var poolId = reader.GetString(0);
            string? tmpl = reader.IsDBNull(1) ? null : reader.GetString(1);

            var resolved = new CodeMappingEntry
            {
                ChainId = chainId,
                Seller = seller,
                Sku = skuNorm,
                PoolId = poolId,
                DownloadUrlTemplate = tmpl
            };

            entry = resolved;

            if (_cache != null)
            {
                _cache.Set(cacheKey, resolved, TimeSpan.FromMinutes(2));
            }

            return true;
        }
        catch (ArgumentException)
        {
            entry = null;
            return false;
        }
        catch (NpgsqlException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            throw;
        }
    }
}
