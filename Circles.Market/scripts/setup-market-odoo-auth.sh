#!/bin/bash
set -e
source "$(dirname "$0")/_lib.sh"

USAGE="<caller_id> <shared_secret> [odoo_origin]

Example:
  $0 market-api-compose my-secret-key http://market-adapter-odoo:5678"

if help_requested "$@"; then
    print_usage "$(basename "$0")" "$USAGE" 0
fi

if [ $# -lt 2 ]; then
    print_usage "$(basename "$0")" "$USAGE"
fi

CALLER_ID="$1"
RAW_KEY="$2"
ODOO_ORIGIN="${3:-http://market-adapter-odoo:5678}"

if [ -z "$CALLER_ID" ]; then
    die "caller_id cannot be empty"
fi

if [ -z "$RAW_KEY" ]; then
    die "shared_secret cannot be empty"
fi

POSTGRES_USER="${POSTGRES_USER:-postgres}"
DB_MARKET="${DB_MARKET_API:-circles_market_api}"
DB_ODOO="${DB_ODOO:-circles_odoo}"

echo "[setup-odoo-auth] Generating SHA256 for Odoo Adapter..."
SHA256=$(printf "%s" "$RAW_KEY" | openssl dgst -sha256 -hex | sed 's/^.* //')

echo "[setup-odoo-auth] Inserting trusted caller into Odoo DB ($DB_ODOO)..."
docker compose exec -T postgres psql -U "$POSTGRES_USER" -d "$DB_ODOO" <<SQL
DELETE FROM trusted_callers WHERE caller_id = '$CALLER_ID';
INSERT INTO trusted_callers (
  caller_id,
  api_key_sha256,
  scopes,
  enabled
) VALUES (
  '$CALLER_ID',
  decode('$SHA256', 'hex'),
  ARRAY['fulfill','inventory'],
  true
);
SQL

echo "[setup-odoo-auth] Inserting outbound credentials into Market API DB ($DB_MARKET)..."
UUID1=$(python3 -c 'import uuid; print(uuid.uuid4())' 2>/dev/null || echo "550e8400-e29b-41d4-a716-446655440003")
UUID2=$(python3 -c 'import uuid; print(uuid.uuid4())' 2>/dev/null || echo "550e8400-e29b-41d4-a716-446655440004")

docker compose exec -T postgres psql -U "$POSTGRES_USER" -d "$DB_MARKET" <<SQL
DELETE FROM outbound_service_credentials WHERE endpoint_origin = '$ODOO_ORIGIN' AND path_prefix IN ('/fulfill','/inventory');

INSERT INTO outbound_service_credentials (
  id, service_kind, endpoint_origin, path_prefix,
  header_name, api_key, enabled
) VALUES
(
  '$UUID1',
  'fulfillment',
  '$ODOO_ORIGIN',
  '/fulfill',
  'X-Circles-Service-Key',
  '$RAW_KEY',
  true
),
(
  '$UUID2',
  'inventory',
  '$ODOO_ORIGIN',
  '/inventory',
  'X-Circles-Service-Key',
  '$RAW_KEY',
  true
);
SQL

echo "[setup-odoo-auth] Done."
