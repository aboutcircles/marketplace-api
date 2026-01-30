#!/bin/bash
set -e
source "$(dirname "$0")/_lib.sh"

USAGE="[db_alias]

Aliases:
  codedisp  -> Show mappings
  odoo      -> Show connections and mappings

Example:
  $0 odoo"

if help_requested "$@"; then
    print_usage "$(basename "$0")" "$USAGE" 0
fi

DB_ALIAS="${1:-all}"
POSTGRES_USER="${POSTGRES_USER:-postgres}"

show_codedisp() {
    echo "--- CodeDispenser Mappings ---"
    docker compose exec -T postgres psql -U "$POSTGRES_USER" -d "${DB_CODEDISP:-circles_codedisp}" -c "SELECT chain_id, seller_address, sku, pool_id, enabled FROM code_mappings;"
}

show_odoo() {
    echo "--- Odoo Connections ---"
    docker compose exec -T postgres psql -U "$POSTGRES_USER" -d "${DB_ODOO:-circles_odoo}" -c "SELECT chain_id, seller_address, odoo_url, odoo_db, enabled FROM odoo_connections;"
    echo "--- Odoo Inventory Mappings ---"
    docker compose exec -T postgres psql -U "$POSTGRES_USER" -d "${DB_ODOO:-circles_odoo}" -c "SELECT chain_id, seller_address, sku, odoo_product_code FROM inventory_mappings;"
}

case "$DB_ALIAS" in
    codedisp) show_codedisp ;;
    odoo) show_odoo ;;
    all) show_codedisp; show_odoo ;;
    *) die "Unknown alias: $DB_ALIAS" ;;
esac
