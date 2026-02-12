#!/bin/bash
set -e
source "$(dirname "$0")/_lib.sh"

USAGE="<chain_id> <seller_address> <sku> <odoo_product_code>

Example:
  $0 1229 0x123... \"GIFT-CARD-100\" \"GC100\""

if help_requested "$@"; then
    print_usage "$(basename "$0")" "$USAGE" 0
fi

if [ $# -lt 4 ]; then
    print_usage "$(basename "$0")" "$USAGE"
fi

CHAIN_ID="$1"
SELLER_ADDRESS=$(echo "$2" | tr '[:upper:]' '[:lower:]')
SKU="$3"
ODOO_PRODUCT_CODE="$4"

if ! is_int "$CHAIN_ID"; then
    die "chain_id must be an integer (got: $CHAIN_ID)"
fi

if ! is_hex_address "$SELLER_ADDRESS"; then
    die "seller_address must be a 0x hex address (got: $SELLER_ADDRESS)"
fi

if [ -z "$SKU" ]; then
    die "sku cannot be empty"
fi

if [ -z "$ODOO_PRODUCT_CODE" ]; then
    die "odoo_product_code cannot be empty"
fi

POSTGRES_USER="${POSTGRES_USER:-postgres}"
DB_ODOO="${DB_ODOO:-circles_odoo}"

docker compose exec -T postgres psql -U "$POSTGRES_USER" -d "$DB_ODOO" <<EOF
INSERT INTO inventory_mappings (
    chain_id,
    seller_address,
    sku,
    odoo_product_code
) VALUES (
    ${CHAIN_ID},
    '${SELLER_ADDRESS}',
    '${SKU}',
    '${ODOO_PRODUCT_CODE}'
)
ON CONFLICT (chain_id, seller_address, sku) DO UPDATE SET
    odoo_product_code = EXCLUDED.odoo_product_code;
EOF

echo "[set-odoo-mapping] Inventory mapping configured for chain=${CHAIN_ID} seller=${SELLER_ADDRESS} sku=${SKU}"
