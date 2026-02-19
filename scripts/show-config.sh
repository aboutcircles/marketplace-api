#!/bin/bash
set -e
source "$(dirname "$0")/_lib.sh"

USAGE="[db_alias]

Aliases:
  market    -> Show outbound credentials
  codedisp  -> Show trusted callers and mappings
  odoo      -> Show trusted callers, connections and mappings

Example:
  $0 odoo"

if help_requested "$@"; then
    print_usage "$(basename "$0")" "$USAGE" 0
fi

DB_ALIAS="${1:-all}"
POSTGRES_USER="${POSTGRES_USER:-postgres}"

show_market() {
    echo "--- Market API Outbound Credentials ---"
    docker compose exec -T postgres psql -U "$POSTGRES_USER" -d "${DB_MARKET_API:-circles_market_api}" -c "SELECT service_kind, endpoint_origin, path_prefix, header_name, enabled FROM outbound_service_credentials;"
}

show_codedisp() {
    echo "--- CodeDispenser Trusted Callers ---"
    docker compose exec -T postgres psql -U "$POSTGRES_USER" -d "${DB_CODEDISP:-circles_codedisp}" -c "SELECT caller_id, scopes, enabled FROM trusted_callers;"
    echo "--- CodeDispenser Mappings ---"
    docker compose exec -T postgres psql -U "$POSTGRES_USER" -d "${DB_CODEDISP:-circles_codedisp}" -c "SELECT chain_id, seller_address, sku, pool_id, enabled FROM code_mappings;"
}

show_odoo() {
    echo "--- Odoo Trusted Callers ---"
    docker compose exec -T postgres psql -U "$POSTGRES_USER" -d "${DB_ODOO:-circles_odoo}" -c "SELECT caller_id, scopes, enabled FROM trusted_callers;"
    echo "--- Odoo Connections ---"
    docker compose exec -T postgres psql -U "$POSTGRES_USER" -d "${DB_ODOO:-circles_odoo}" -c "SELECT chain_id, seller_address, odoo_url, odoo_db, enabled FROM odoo_connections;"
    echo "--- Odoo Inventory Mappings ---"
    docker compose exec -T postgres psql -U "$POSTGRES_USER" -d "${DB_ODOO:-circles_odoo}" -c "SELECT chain_id, seller_address, sku, odoo_product_code FROM inventory_mappings;"
}

case "$DB_ALIAS" in
    market) show_market ;;
    codedisp) show_codedisp ;;
    odoo) show_odoo ;;
    all) show_market; show_codedisp; show_odoo ;;
    *) die "Unknown alias: $DB_ALIAS" ;;
esac
