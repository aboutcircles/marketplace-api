using Npgsql;

namespace Circles.Market.Adapters.Odoo.Db;

public sealed class OdooDbBootstrapper
{
    private readonly string _connString;
    private readonly ILogger<OdooDbBootstrapper> _log;

    public OdooDbBootstrapper(string connString, ILogger<OdooDbBootstrapper> log)
    {
        _connString = connString ?? throw new ArgumentNullException(nameof(connString));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        _log.LogInformation("EnsureSchemaAsync starting for Odoo tables...");
        try
        {
            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);
            _log.LogInformation("Postgres connection opened for Odoo schema creation.");
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
-- 1. inventory_mappings (SKU mapping)
CREATE TABLE IF NOT EXISTS inventory_mappings (
  seller_address     text NOT NULL,
  chain_id           bigint NOT NULL,
  sku                text NOT NULL,
  odoo_product_code  text NOT NULL,
  enabled            boolean NOT NULL DEFAULT true,
  created_at         timestamptz NOT NULL DEFAULT now(),
  revoked_at         timestamptz NULL,
  PRIMARY KEY (seller_address, chain_id, sku)
);

CREATE INDEX IF NOT EXISTS ix_inventory_mappings_lookup ON inventory_mappings(seller_address, chain_id, sku) WHERE (enabled = true AND revoked_at IS NULL);

-- 2. odoo_connections (Odoo connection)
CREATE TABLE IF NOT EXISTS odoo_connections (
  seller_address text NOT NULL,
  chain_id       bigint NOT NULL,
  odoo_url       text NOT NULL,
  odoo_db        text NOT NULL,
  odoo_uid       integer NULL,
  odoo_key       text NOT NULL,
  sale_partner_id integer NULL,
  jsonrpc_timeout_ms integer NOT NULL DEFAULT 30000,
  fulfill_inherit_request_abort boolean NOT NULL DEFAULT false,
  enabled        boolean NOT NULL DEFAULT true,
  created_at     timestamptz NOT NULL DEFAULT now(),
  revoked_at     timestamptz NULL,
  PRIMARY KEY (seller_address, chain_id)
);

-- 3. fulfillment_runs (idempotency tracking)
CREATE TABLE IF NOT EXISTS fulfillment_runs (
  chain_id          bigint NOT NULL,
  seller_address    text NOT NULL,
  payment_reference text NOT NULL,
  order_id          text NOT NULL,
  status            text NOT NULL, -- 'started', 'ok', 'error'
  last_error        text NULL,
  odoo_order_id     integer NULL,
  odoo_order_name   text NULL,
  created_at        timestamptz NOT NULL DEFAULT now(),
  updated_at        timestamptz NOT NULL DEFAULT now(),
  completed_at      timestamptz NULL,
  PRIMARY KEY (chain_id, seller_address, payment_reference)
);
";
            await cmd.ExecuteNonQueryAsync(ct);
            _log.LogInformation("Odoo schema created/verified successfully.");
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "Failed to ensure Odoo schema. Service cannot start.");
            throw;
        }
    }
}
