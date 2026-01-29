#!/bin/bash
set -e
source "$(dirname "$0")/_lib.sh"

USAGE="<chain_id> <operator_address>

Example:
  $0 100 0x1234567890123456789012345678901234567890

This script adds a trusted operator to the market API database.
The operator address can be with or without the eip155:chainId: prefix."

if help_requested "$@"; then
    print_usage "$(basename "$0")" "$USAGE" 0
fi

if [ $# -lt 2 ]; then
    print_usage "$(basename "$0")" "$USAGE"
fi

CHAIN_ID="$1"
OP_ADDR="$2"

if [ -z "$CHAIN_ID" ]; then
    die "chain_id cannot be empty"
fi

if [ -z "$OP_ADDR" ]; then
    die "operator_address cannot be empty"
fi

# Normalize operator address (remove eip155:prefix if present)
OP_NORMALIZED=$(echo "$OP_ADDR" | sed 's/^epi155://' | sed 's/^eip155:[0-9]*://')
OP_KEY="eip155:${CHAIN_ID}:${OP_NORMALIZED}"

POSTGRES_USER="${POSTGRES_USER:-postgres}"
DB_MARKET="${DB_MARKET_API:-circles_market_api}"

echo "[setup-trusted-operator] Adding trusted operator $OP_ADDR on chain $CHAIN_ID..."

docker compose exec -T postgres psql -U "$POSTGRES_USER" -d "$DB_MARKET" <<SQL
INSERT INTO trusted_operators (operator_address, enabled)
VALUES ('$OP_KEY', true)
ON CONFLICT (operator_address) DO UPDATE SET enabled = true, updated_at = NOW();
SQL

echo "[setup-trusted-operator] Done. Operator $OP_KEY is now trusted."
