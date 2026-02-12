#!/bin/bash
set -e
source "$(dirname "$0")/_lib.sh"

USAGE="<caller_id> <shared_secret> [codedisp_origin]

Example:
  $0 market-api-compose my-secret-key http://market-adapter-codedispenser:5680"

if help_requested "$@"; then
    print_usage "$(basename "$0")" "$USAGE" 0
fi

if [ $# -lt 2 ]; then
    print_usage "$(basename "$0")" "$USAGE"
fi

CALLER_ID="$1"
RAW_KEY="$2"
CODEDISP_ORIGIN="${3:-http://market-adapter-codedispenser:5680}"

if [ -z "$CALLER_ID" ]; then
    die "caller_id cannot be empty"
fi

if [ -z "$RAW_KEY" ]; then
    die "shared_secret cannot be empty"
fi

POSTGRES_USER="${POSTGRES_USER:-postgres}"
DB_MARKET="${DB_MARKET_API:-circles_market_api}"
DB_CODEDISP="${DB_CODEDISP:-circles_codedisp}"

echo "[setup-auth] Generating SHA256 for CodeDispenser..."
SHA256=$(printf "%s" "$RAW_KEY" | openssl dgst -sha256 -hex | sed 's/^.* //')

echo "[setup-auth] Inserting trusted caller into CodeDispenser DB ($DB_CODEDISP)..."
docker compose exec -T postgres psql -U "$POSTGRES_USER" -d "$DB_CODEDISP" <<SQL
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

echo "[setup-auth] Inserting outbound credentials into Market API DB ($DB_MARKET)..."
UUID1=$(python3 -c 'import uuid; print(uuid.uuid4())' 2>/dev/null || echo "550e8400-e29b-41d4-a716-446655440000")
UUID2=$(python3 -c 'import uuid; print(uuid.uuid4())' 2>/dev/null || echo "550e8400-e29b-41d4-a716-446655440001")

docker compose exec -T postgres psql -U "$POSTGRES_USER" -d "$DB_MARKET" <<SQL
DELETE FROM outbound_service_credentials WHERE endpoint_origin = '$CODEDISP_ORIGIN' AND path_prefix IN ('/fulfill','/inventory');

INSERT INTO outbound_service_credentials (
  id, service_kind, endpoint_origin, path_prefix,
  header_name, api_key, enabled
) VALUES
(
  '$UUID1',
  'fulfillment',
  '$CODEDISP_ORIGIN',
  '/fulfill',
  'X-Circles-Service-Key',
  '$RAW_KEY',
  true
),
(
  '$UUID2',
  'inventory',
  '$CODEDISP_ORIGIN',
  '/inventory',
  'X-Circles-Service-Key',
  '$RAW_KEY',
  true
);
SQL

echo "[setup-auth] Done."
