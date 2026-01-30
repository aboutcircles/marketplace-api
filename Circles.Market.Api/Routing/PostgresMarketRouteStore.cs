using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Circles.Market.Api.Routing;

public sealed class PostgresMarketRouteStore : IMarketRouteStore
{
    private readonly string _connString;
    private readonly ILogger<PostgresMarketRouteStore> _log;
    private readonly IMemoryCache _cache;

    public PostgresMarketRouteStore(string connString, ILogger<PostgresMarketRouteStore> log, IMemoryCache cache)
    {
        _connString = connString ?? throw new ArgumentNullException(nameof(connString));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        _log.LogInformation("EnsureSchemaAsync starting for market_service_routes...");
        try
        {
            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"CREATE TABLE IF NOT EXISTS market_service_routes (
  chain_id         bigint  NOT NULL,
  seller_address   text    NOT NULL,
  sku              text    NOT NULL,
  inventory_url    text    NULL,
  availability_url text    NULL,
  fulfillment_url  text    NULL,
  is_one_off       boolean NOT NULL DEFAULT false,
  enabled          boolean NOT NULL DEFAULT true,
  created_at       timestamptz NOT NULL DEFAULT now(),
  updated_at       timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (chain_id, seller_address, sku)
);

CREATE INDEX IF NOT EXISTS ix_market_routes_enabled
  ON market_service_routes(enabled, chain_id, seller_address, sku);
";

            await cmd.ExecuteNonQueryAsync(ct);
            _log.LogInformation("EnsureSchemaAsync completed successfully for market_service_routes.");
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "FATAL: EnsureSchemaAsync failed for market_service_routes.");
            throw;
        }
    }

    public async Task<MarketRouteConfig?> TryGetAsync(long chainId, string sellerAddress, string sku, CancellationToken ct = default)
    {
        if (chainId <= 0) return null;
        if (string.IsNullOrWhiteSpace(sellerAddress) || string.IsNullOrWhiteSpace(sku)) return null;

        sellerAddress = sellerAddress.Trim().ToLowerInvariant();
        sku = sku.Trim().ToLowerInvariant();

        string cacheKey = $"mr:{chainId}:{sellerAddress}:{sku}";
        if (_cache.TryGetValue<MarketRouteConfig?>(cacheKey, out var cached))
        {
            return cached;
        }

        MarketRouteConfig? result = null;
        await using (var conn = new NpgsqlConnection(_connString))
        {
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT chain_id, seller_address, sku, inventory_url, availability_url, fulfillment_url, is_one_off, enabled
FROM market_service_routes
WHERE enabled = true
  AND chain_id = $1
  AND seller_address = $2
  AND sku = $3
LIMIT 1";
            cmd.Parameters.AddWithValue(chainId);
            cmd.Parameters.AddWithValue(sellerAddress);
            cmd.Parameters.AddWithValue(sku);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                result = new MarketRouteConfig(
                    ChainId: reader.GetInt64(0),
                    SellerAddress: reader.GetString(1),
                    Sku: reader.GetString(2),
                    InventoryUrl: reader.IsDBNull(3) ? null : reader.GetString(3),
                    AvailabilityUrl: reader.IsDBNull(4) ? null : reader.GetString(4),
                    FulfillmentUrl: reader.IsDBNull(5) ? null : reader.GetString(5),
                    IsOneOff: reader.GetBoolean(6),
                    Enabled: reader.GetBoolean(7));
            }
        }

        _cache.Set(cacheKey, result, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = result is null ? TimeSpan.FromSeconds(20) : TimeSpan.FromMinutes(2),
            Size = 200
        });

        return result;
    }

    public async Task<bool> IsConfiguredAsync(long chainId, string sellerAddress, string sku, CancellationToken ct = default)
    {
        if (chainId <= 0) { return false; }
        if (string.IsNullOrWhiteSpace(sellerAddress) || string.IsNullOrWhiteSpace(sku)) { return false; }

        string sellerNorm = sellerAddress.Trim().ToLowerInvariant();
        string skuNorm = sku.Trim().ToLowerInvariant();

        var cfg = await TryGetAsync(chainId, sellerNorm, skuNorm, ct);
        return cfg is not null && cfg.IsConfigured;
    }

    public async Task<string?> TryResolveUpstreamAsync(
        long chainId,
        string sellerAddress,
        string sku,
        MarketServiceKind serviceKind,
        CancellationToken ct = default)
    {
        if (chainId <= 0) { return null; }
        if (string.IsNullOrWhiteSpace(sellerAddress) || string.IsNullOrWhiteSpace(sku)) { return null; }

        string sellerNorm = sellerAddress.Trim().ToLowerInvariant();
        string skuNorm = sku.Trim().ToLowerInvariant();

        var cfg = await TryGetAsync(chainId, sellerNorm, skuNorm, ct);
        if (cfg is null || !cfg.IsConfigured)
        {
            return null;
        }

        return serviceKind switch
        {
            MarketServiceKind.Inventory => string.IsNullOrWhiteSpace(cfg.InventoryUrl) ? null : cfg.InventoryUrl,
            MarketServiceKind.Availability => string.IsNullOrWhiteSpace(cfg.AvailabilityUrl) ? null : cfg.AvailabilityUrl,
            MarketServiceKind.Fulfillment => string.IsNullOrWhiteSpace(cfg.FulfillmentUrl) ? null : cfg.FulfillmentUrl,
            _ => null
        };
    }
}
