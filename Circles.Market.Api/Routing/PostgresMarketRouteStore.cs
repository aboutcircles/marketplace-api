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
            cmd.CommandText = @"
-- Offer types: templates for upstream adapters
CREATE TABLE IF NOT EXISTS offer_types (
  offer_type                text    NOT NULL PRIMARY KEY,
  inventory_url_template     text    NULL,
  availability_url_template  text    NULL,
  fulfillment_url_template   text    NULL,
  enabled                   boolean NOT NULL DEFAULT true
);

CREATE INDEX IF NOT EXISTS ix_offer_types_enabled
  ON offer_types(enabled, offer_type);

-- Routes: configured SKUs
CREATE TABLE IF NOT EXISTS market_service_routes (
  chain_id         bigint  NOT NULL,
  seller_address   text    NOT NULL,
  sku              text    NOT NULL,
  offer_type       text    NULL,
  is_one_off       boolean NOT NULL DEFAULT false,
  enabled          boolean NOT NULL DEFAULT true,
  PRIMARY KEY (chain_id, seller_address, sku),
  CONSTRAINT ck_market_service_routes_offer_type_or_one_off
    CHECK (is_one_off OR offer_type IS NOT NULL),
  CONSTRAINT fk_market_service_routes_offer_type
    FOREIGN KEY (offer_type) REFERENCES offer_types(offer_type)
);

CREATE INDEX IF NOT EXISTS ix_market_routes_enabled
  ON market_service_routes(enabled, chain_id, seller_address, sku);

-- Seed canonical offer types (operator can override by updating rows)
INSERT INTO offer_types (offer_type, inventory_url_template, availability_url_template, fulfillment_url_template, enabled)
VALUES
  (
    'odoo',
    'http://market-adapter-odoo:{MARKET_ODOO_ADAPTER_PORT}/inventory/{chain_id}/{seller}/{sku}',
    'http://market-adapter-odoo:{MARKET_ODOO_ADAPTER_PORT}/availability/{chain_id}/{seller}/{sku}',
    'http://market-adapter-odoo:{MARKET_ODOO_ADAPTER_PORT}/fulfill/{chain_id}/{seller}',
    true
  ),
  (
    'codedispenser',
    'http://market-adapter-codedispenser:{MARKET_CODE_DISPENSER_PORT}/inventory/{chain_id}/{seller}/{sku}',
    NULL,
    'http://market-adapter-codedispenser:{MARKET_CODE_DISPENSER_PORT}/fulfill/{chain_id}/{seller}',
    true
  )
ON CONFLICT (offer_type) DO NOTHING;
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
            cmd.CommandText = @"
SELECT chain_id, seller_address, sku, offer_type, is_one_off, enabled
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
                    OfferType: reader.IsDBNull(3) ? null : reader.GetString(3),
                    IsOneOff: reader.GetBoolean(4),
                    Enabled: reader.GetBoolean(5));
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
        if (cfg is null)
        {
            return false;
        }

        if (!cfg.Enabled)
        {
            return false;
        }

        if (cfg.IsOneOff)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(cfg.OfferType))
        {
            return false;
        }

        var ot = await TryGetOfferTypeAsync(cfg.OfferType, ct);
        return ot is not null && ot.Enabled;
    }

    private sealed record OfferTypeRow(
        string OfferType,
        string? InventoryTemplate,
        string? AvailabilityTemplate,
        string? FulfillmentTemplate,
        bool Enabled);

    private async Task<OfferTypeRow?> TryGetOfferTypeAsync(string offerType, CancellationToken ct)
    {
        string key = $"ot:{offerType.Trim().ToLowerInvariant()}";
        if (_cache.TryGetValue<OfferTypeRow?>(key, out var cached))
        {
            return cached;
        }

        OfferTypeRow? result = null;

        await using (var conn = new NpgsqlConnection(_connString))
        {
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT offer_type, inventory_url_template, availability_url_template, fulfillment_url_template, enabled
FROM offer_types
WHERE offer_type = $1
LIMIT 1";
            cmd.Parameters.AddWithValue(offerType.Trim().ToLowerInvariant());

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                result = new OfferTypeRow(
                    OfferType: reader.GetString(0),
                    InventoryTemplate: reader.IsDBNull(1) ? null : reader.GetString(1),
                    AvailabilityTemplate: reader.IsDBNull(2) ? null : reader.GetString(2),
                    FulfillmentTemplate: reader.IsDBNull(3) ? null : reader.GetString(3),
                    Enabled: reader.GetBoolean(4));
            }
        }

        _cache.Set(key, result, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = result is null ? TimeSpan.FromSeconds(20) : TimeSpan.FromMinutes(10),
            Size = 50
        });

        return result;
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

        // One-off items use internal one-off logic and do not have upstream adapter URLs.
        if (cfg.IsOneOff)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(cfg.OfferType))
        {
            return null;
        }

        var ot = await TryGetOfferTypeAsync(cfg.OfferType, ct);
        if (ot is null || !ot.Enabled)
        {
            return null;
        }

        string? template = serviceKind switch
        {
            MarketServiceKind.Inventory => ot.InventoryTemplate,
            MarketServiceKind.Availability => ot.AvailabilityTemplate,
            MarketServiceKind.Fulfillment => ot.FulfillmentTemplate,
            _ => null
        };

        if (string.IsNullOrWhiteSpace(template))
        {
            return null;
        }

        if (!OfferTypeTemplateExpander.TryExpand(template, chainId, sellerNorm, skuNorm, out var expanded, out var err))
        {
            _log.LogWarning("Failed to expand offer type template for offerType={OfferType} kind={Kind}: {Error}",
                cfg.OfferType, serviceKind, err);
            return null;
        }

        return expanded;
    }
}
