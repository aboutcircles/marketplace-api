#!/bin/bash
set -e
source "$(dirname "$0")/_lib.sh"

USAGE="<chain_id> <seller_address> <sku> <pool_id> [download_url_template]

Example:
  $0 1229 0x636f44a378da9256a128b104b1e06aa50a578f33 my-sku my-pool"

if help_requested "$@"; then
    print_usage "$(basename "$0")" "$USAGE" 0
fi

if [ $# -lt 4 ]; then
    print_usage "$(basename "$0")" "$USAGE"
fi

CHAIN_ID="$1"
SELLER="$2"
SKU="$3"
POOL_ID="$4"
URL_TEMPLATE="${5:-http://{code}}"

if ! is_int "$CHAIN_ID"; then
    die "chain_id must be an integer (got: $CHAIN_ID)"
fi

if ! is_hex_address "$SELLER"; then
    die "seller_address must be a 0x hex address (got: $SELLER)"
fi

if [ -z "$SKU" ]; then
    die "sku cannot be empty"
fi

if [ -z "$POOL_ID" ]; then
    die "pool_id cannot be empty"
fi

POSTGRES_USER="${POSTGRES_USER:-postgres}"
DB_CODEDISP="${DB_CODEDISP:-circles_codedisp}"

echo "[set-mapping] Inserting/Updating mapping in CodeDispenser DB ($DB_CODEDISP)..."
docker compose exec -T postgres psql -U "$POSTGRES_USER" -d "$DB_CODEDISP" <<SQL
INSERT INTO code_pools(pool_id) VALUES ('$POOL_ID') ON CONFLICT DO NOTHING;

INSERT INTO code_mappings (
  chain_id, seller_address, sku, pool_id, download_url_template, enabled, created_at
) VALUES (
  $CHAIN_ID, lower('$SELLER'), lower('$SKU'), '$POOL_ID', '$URL_TEMPLATE', true, now()
)
ON CONFLICT (chain_id, seller_address, sku) DO UPDATE SET
  pool_id = EXCLUDED.pool_id,
  download_url_template = EXCLUDED.download_url_template,
  enabled = EXCLUDED.enabled;
SQL

echo "[set-mapping] Done."
