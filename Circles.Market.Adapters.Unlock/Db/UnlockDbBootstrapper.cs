using Npgsql;

namespace Circles.Market.Adapters.Unlock.Db;

public sealed class UnlockDbBootstrapper
{
    private readonly string _connString;
    private readonly ILogger<UnlockDbBootstrapper> _log;

    public UnlockDbBootstrapper(string connString, ILogger<UnlockDbBootstrapper> log)
    {
        _connString = connString ?? throw new ArgumentNullException(nameof(connString));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        _log.LogInformation("EnsureSchemaAsync starting for Unlock adapter tables...");

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS unlock_mappings (
  chain_id              bigint NOT NULL,
  seller_address        text NOT NULL,
  sku                   text NOT NULL,
  lock_address          text NOT NULL,
  rpc_url               text NOT NULL,
  service_private_key   text NOT NULL,
  duration_seconds      bigint NULL,
  expiration_unix       bigint NULL,
  key_manager_mode      text NOT NULL DEFAULT 'buyer',
  fixed_key_manager     text NULL,
  locksmith_base        text NOT NULL DEFAULT 'https://locksmith.unlock-protocol.com',
  locksmith_token       text NULL,
  max_supply            bigint NOT NULL,
  enabled               boolean NOT NULL DEFAULT true,
  created_at            timestamptz NOT NULL DEFAULT now(),
  revoked_at            timestamptz NULL,
  PRIMARY KEY (chain_id, seller_address, sku),
  CONSTRAINT ck_unlock_mappings_duration_or_expiration
    CHECK ((duration_seconds IS NULL) <> (expiration_unix IS NULL)),
  CONSTRAINT ck_unlock_mappings_duration_positive
    CHECK (duration_seconds IS NULL OR duration_seconds > 0),
  CONSTRAINT ck_unlock_mappings_expiration_positive
    CHECK (expiration_unix IS NULL OR expiration_unix > 0),
  CONSTRAINT ck_unlock_mappings_key_manager_mode
    CHECK (key_manager_mode IN ('buyer', 'service', 'fixed')),
  CONSTRAINT ck_unlock_mappings_max_supply_non_negative
    CHECK (max_supply >= 0),
  CONSTRAINT ck_unlock_mappings_fixed_key_manager_required
    CHECK ((key_manager_mode <> 'fixed') OR (fixed_key_manager IS NOT NULL AND length(trim(fixed_key_manager)) > 0))
);

CREATE INDEX IF NOT EXISTS ix_unlock_mappings_enabled
  ON unlock_mappings(chain_id, seller_address, sku)
  WHERE enabled = true AND revoked_at IS NULL;

CREATE TABLE IF NOT EXISTS unlock_fulfillment_runs (
  chain_id          bigint NOT NULL,
  seller_address    text NOT NULL,
  payment_reference text NOT NULL,
  order_id          text NOT NULL,
  status            text NOT NULL,
  last_error        text NULL,
  created_at        timestamptz NOT NULL DEFAULT now(),
  updated_at        timestamptz NOT NULL DEFAULT now(),
  completed_at      timestamptz NULL,
  PRIMARY KEY (chain_id, seller_address, payment_reference)
);

CREATE TABLE IF NOT EXISTS unlock_mints (
  chain_id          bigint NOT NULL,
  seller_address    text NOT NULL,
  payment_reference text NOT NULL,
  order_id          text NOT NULL,
  sku               text NOT NULL,
  buyer_address     text NOT NULL,
  lock_address      text NOT NULL,
  quantity          bigint NOT NULL DEFAULT 1,
  transaction_hash  text NULL,
  key_id            text NULL,
  expiration_unix   bigint NULL,
  status            text NOT NULL,
  warning           text NULL,
  error             text NULL,
  response_json     jsonb NULL,
  created_at        timestamptz NOT NULL DEFAULT now(),
  updated_at        timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (chain_id, seller_address, payment_reference)
);

ALTER TABLE unlock_mints
  ADD COLUMN IF NOT EXISTS quantity bigint;

UPDATE unlock_mints
SET quantity = 1
WHERE quantity IS NULL;

ALTER TABLE unlock_mints
  ALTER COLUMN quantity SET DEFAULT 1;

ALTER TABLE unlock_mints
  ALTER COLUMN quantity SET NOT NULL;

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1
    FROM pg_constraint
    WHERE conname = 'ck_unlock_mints_quantity_positive'
      AND conrelid = 'unlock_mints'::regclass
  ) THEN
    ALTER TABLE unlock_mints
      ADD CONSTRAINT ck_unlock_mints_quantity_positive CHECK (quantity > 0);
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS ix_unlock_mints_sold_lookup
  ON unlock_mints(chain_id, seller_address, sku)
  WHERE status = 'ok';

CREATE TABLE IF NOT EXISTS unlock_mint_tickets (
  chain_id          bigint NOT NULL,
  seller_address    text NOT NULL,
  payment_reference text NOT NULL,
  ticket_index      integer NOT NULL,
  order_id          text NOT NULL,
  sku               text NOT NULL,
  buyer_address     text NOT NULL,
  lock_address      text NOT NULL,
  transaction_hash  text NULL,
  key_id            text NULL,
  expiration_unix   bigint NULL,
  status            text NOT NULL,
  warning           text NULL,
  error             text NULL,
  ticket_json       jsonb NULL,
  qrcode_data_url   text NULL,
  created_at        timestamptz NOT NULL DEFAULT now(),
  updated_at        timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT uq_unlock_mint_tickets_payment_ticket_index
      UNIQUE (chain_id, seller_address, payment_reference, ticket_index)
);

CREATE INDEX IF NOT EXISTS ix_unlock_mint_tickets_payment
  ON unlock_mint_tickets(chain_id, seller_address, payment_reference);

CREATE INDEX IF NOT EXISTS ix_unlock_mint_tickets_sku_status
  ON unlock_mint_tickets(chain_id, seller_address, sku, status);
";

        await cmd.ExecuteNonQueryAsync(ct);
        _log.LogInformation("EnsureSchemaAsync completed successfully for Unlock adapter.");
    }
}
