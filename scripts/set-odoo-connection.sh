#!/bin/bash
set -e
source "$(dirname "$0")/_lib.sh"

USAGE="<chain_id> <seller_address> <odoo_url> <odoo_db> <odoo_uid> <odoo_key> [partner_id] [timeout_ms] [inherit_abort]

Example:
  $0 1229 0xabc... https://your.odoo.com yourdb 7 yourkey 123 30000 false"

if help_requested "$@"; then
    print_usage "$(basename "$0")" "$USAGE" 0
fi

if [ $# -lt 6 ]; then
    print_usage "$(basename "$0")" "$USAGE"
fi

CHAIN_ID="$1"
SELLER_ADDRESS="$(echo "$2" | tr '[:upper:]' '[:lower:]')"
ODOO_URL="$3"
ODOO_DB="$4"
ODOO_UID="$5"
ODOO_KEY="$6"
SALE_PARTNER_ID="${7:-NULL}"
JSONRPC_TIMEOUT_MS="${8:-30000}"
FULFILL_INHERIT_REQUEST_ABORT="${9:-false}"

if ! is_int "$CHAIN_ID"; then
    die "chain_id must be an integer (got: $CHAIN_ID)"
fi

if ! is_hex_address "$SELLER_ADDRESS"; then
    die "seller_address must be a 0x hex address (got: $SELLER_ADDRESS)"
fi

if [ -z "$ODOO_URL" ]; then
    die "odoo_url cannot be empty"
fi

ODOO_UID_SQL="${ODOO_UID:-NULL}"
if [ "$ODOO_UID_SQL" = '""' ]; then ODOO_UID_SQL="NULL"; fi

POSTGRES_USER="${POSTGRES_USER:-postgres}"
DB_ODOO="${DB_ODOO:-circles_odoo}"

echo "[set-odoo-connection] Upserting into DB '$DB_ODOO' (chain_id=$CHAIN_ID seller=$SELLER_ADDRESS)..."

docker compose exec -T postgres psql -U "$POSTGRES_USER" -d "$DB_ODOO" <<SQL
INSERT INTO odoo_connections (
  seller_address,
  chain_id,
  odoo_url,
  odoo_db,
  odoo_uid,
  odoo_key,
  sale_partner_id,
  jsonrpc_timeout_ms,
  fulfill_inherit_request_abort,
  enabled,
  revoked_at
) VALUES (
  '$SELLER_ADDRESS',
  $CHAIN_ID,
  '$ODOO_URL',
  '$ODOO_DB',
  $ODOO_UID_SQL,
  '$ODOO_KEY',
  $SALE_PARTNER_ID,
  $JSONRPC_TIMEOUT_MS,
  $FULFILL_INHERIT_REQUEST_ABORT,
  true,
  NULL
)
ON CONFLICT (seller_address, chain_id) DO UPDATE SET
  odoo_url = EXCLUDED.odoo_url,
  odoo_db = EXCLUDED.odoo_db,
  odoo_uid = EXCLUDED.odoo_uid,
  odoo_key = EXCLUDED.odoo_key,
  sale_partner_id = EXCLUDED.sale_partner_id,
  jsonrpc_timeout_ms = EXCLUDED.jsonrpc_timeout_ms,
  fulfill_inherit_request_abort = EXCLUDED.fulfill_inherit_request_abort,
  enabled = true,
  revoked_at = NULL;
SQL

echo "[set-odoo-connection] Done."
