using Npgsql;

namespace Circles.Market.Adapters.WooCommerce.Db;

public sealed class WooCommerceDbBootstrapper
{
    private readonly string _connString;
    private readonly ILogger<WooCommerceDbBootstrapper> _log;

    public WooCommerceDbBootstrapper(string connString, ILogger<WooCommerceDbBootstrapper> log)
    {
        _connString = connString ?? throw new ArgumentNullException(nameof(connString));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        _log.LogInformation("EnsureSchemaAsync starting for WooCommerce tables...");
        try
        {
            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                -- 1. wc_connections: per-seller WooCommerce credentials
                CREATE TABLE IF NOT EXISTS wc_connections (
                    id                              uuid NOT NULL DEFAULT gen_random_uuid(),
                    chain_id                        bigint NOT NULL,
                    seller_address                  text NOT NULL,
                    wc_base_url                     text NOT NULL,
                    wc_consumer_key                text NOT NULL,
                    wc_consumer_secret             text NOT NULL,
                    default_customer_id             integer NULL,
                    order_status                    text NOT NULL DEFAULT 'pending',
                    timeout_ms                      integer NOT NULL DEFAULT 30000,
                    fulfill_inherit_request_abort  boolean NOT NULL DEFAULT true,
                    enabled                         boolean NOT NULL DEFAULT true,
                    created_at                      timestamptz NOT NULL DEFAULT now(),
                    revoked_at                      timestamptz NULL,
                    PRIMARY KEY (id)
                );

                CREATE UNIQUE INDEX IF NOT EXISTS ix_wc_connections_seller
                    ON wc_connections(chain_id, seller_address)
                    WHERE revoked_at IS NULL;

                CREATE INDEX IF NOT EXISTS ix_wc_connections_enabled
                    ON wc_connections(chain_id, seller_address, enabled)
                    WHERE enabled = true;

                -- 2. wc_product_mappings: SKU → WC product
                CREATE TABLE IF NOT EXISTS wc_product_mappings (
                    id              uuid NOT NULL DEFAULT gen_random_uuid(),
                    chain_id        bigint NOT NULL,
                    seller_address  text NOT NULL,
                    sku             text NOT NULL,
                    wc_product_sku  text NOT NULL,
                    wc_product_id   integer NULL,
                    enabled         boolean NOT NULL DEFAULT true,
                    created_at      timestamptz NOT NULL DEFAULT now(),
                    revoked_at      timestamptz NULL,
                    PRIMARY KEY (id)
                );

                CREATE UNIQUE INDEX IF NOT EXISTS ix_wc_product_mappings_sku
                    ON wc_product_mappings(chain_id, seller_address, sku)
                    WHERE revoked_at IS NULL;

                CREATE INDEX IF NOT EXISTS ix_wc_product_mappings_lookup
                    ON wc_product_mappings(chain_id, seller_address, sku)
                    WHERE enabled = true AND revoked_at IS NULL;

                -- 3. wc_fulfillment_runs: idempotency tracking
                CREATE TABLE IF NOT EXISTS wc_fulfillment_runs (
                    id                  uuid NOT NULL DEFAULT gen_random_uuid(),
                    chain_id            bigint NOT NULL,
                    seller_address      text NOT NULL,
                    payment_reference   text NOT NULL,
                    idempotency_key     uuid NOT NULL UNIQUE,
                    wc_order_id        integer NULL,
                    wc_order_number    text NULL,
                    status              text NOT NULL,          -- pending | completed | failed
                    outcome             text NULL,             -- success | already_fulfilled | validation_error | wc_api_error
                    error_detail        text NULL,
                    request_payload     jsonb NOT NULL DEFAULT '{}',
                    response_payload    jsonb NULL,
                    created_at          timestamptz NOT NULL DEFAULT now(),
                    completed_at        timestamptz NULL,
                    PRIMARY KEY (id)
                );

                -- idempotency_key UNIQUE constraint is implicit from column definition

                CREATE UNIQUE INDEX IF NOT EXISTS ix_wc_fulfillment_runs_lookup
                    ON wc_fulfillment_runs(chain_id, seller_address, payment_reference);

                -- 4. wc_inventory_stock: local stock overrides
                CREATE TABLE IF NOT EXISTS wc_inventory_stock (
                    id              uuid NOT NULL DEFAULT gen_random_uuid(),
                    chain_id        bigint NOT NULL,
                    seller_address  text NOT NULL,
                    sku             text NOT NULL,
                    stock_quantity  integer NOT NULL,  -- -1 = unlimited
                    updated_at      timestamptz NOT NULL DEFAULT now(),
                    PRIMARY KEY (id)
                );

                CREATE UNIQUE INDEX IF NOT EXISTS ix_wc_inventory_stock_sku
                    ON wc_inventory_stock(chain_id, seller_address, sku);

                -- 5. wc_customers: buyer details stored on fulfill
                CREATE TABLE IF NOT EXISTS wc_customers (
                    id               uuid NOT NULL DEFAULT gen_random_uuid(),
                    chain_id         bigint NOT NULL,
                    seller_address   text NOT NULL,
                    buyer_address    text NOT NULL,
                    email            text NOT NULL,
                    given_name       text NULL,
                    family_name      text NULL,
                    telephone        text NULL,
                    street_address   text NULL,
                    address_locality text NULL,
                    postal_code      text NULL,
                    address_country  text NULL,
                    wc_customer_id   integer NULL,
                    created_at       timestamptz NOT NULL DEFAULT now(),
                    updated_at       timestamptz NOT NULL DEFAULT now(),
                    PRIMARY KEY (id)
                );

                CREATE UNIQUE INDEX IF NOT EXISTS ix_wc_customers_buyer
                    ON wc_customers(chain_id, seller_address, buyer_address);

                CREATE INDEX IF NOT EXISTS ix_wc_customers_seller
                    ON wc_customers(chain_id, seller_address);
                """;
            await cmd.ExecuteNonQueryAsync(ct);
            _log.LogInformation("WooCommerce schema created/verified successfully.");
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "Failed to ensure WooCommerce schema. Service cannot start.");
            throw;
        }
    }
}