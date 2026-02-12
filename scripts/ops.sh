#!/bin/bash
set -e
source "$(dirname "$0")/_lib.sh"

USAGE="<command> [args...]

Commands:
  auth-codedisp       Wire Market API -> CodeDispenser auth
  mapping-codedisp    Create CodeDispenser mapping
  seed-pool           Seed CodeDispenser code pool
  auth-odoo           Wire Market API -> Odoo auth
  odoo-connection     Configure Odoo connection
  odoo-mapping        Configure Odoo inventory mapping
  psql <db>           Open PSQL shell (market|codedisp|odoo)
  show [db]           Inspect configuration
  status              High-level operator status view
  doctor              Check system health and prerequisites

Example:
  $0 auth-codedisp market-api-compose my-secret"

if help_requested "$@"; then
    print_usage "$(basename "$0")" "$USAGE" 0
fi

if [ $# -lt 1 ]; then
    print_usage "$(basename "$0")" "$USAGE"
fi

show_status() {
    echo "=== Circles.Market Operator Status ==="
    echo ""
    echo "--- Containers ---"
    docker compose ps
    echo ""

    POSTGRES_USER="${POSTGRES_USER:-postgres}"
    DB_MARKET="${DB_MARKET_API:-circles_market_api}"
    DB_CODEDISP="${DB_CODEDISP:-circles_codedisp}"
    DB_ODOO="${DB_ODOO:-circles_odoo}"

    if docker compose ps postgres | grep -q "Up"; then
        echo "--- Database Summary ---"
        echo "Market Outbound Creds: $(docker compose exec -T postgres psql -U "$POSTGRES_USER" -d "$DB_MARKET" -t -c "SELECT count(*) FROM outbound_service_credentials;" | xargs)"
        echo "CodeDispenser Callers: $(docker compose exec -T postgres psql -U "$POSTGRES_USER" -d "$DB_CODEDISP" -t -c "SELECT count(*) FROM trusted_callers;" | xargs)"
        echo "CodeDispenser Mappings: $(docker compose exec -T postgres psql -U "$POSTGRES_USER" -d "$DB_CODEDISP" -t -c "SELECT count(*) FROM code_mappings;" | xargs)"
        echo "Odoo Connections: $(docker compose exec -T postgres psql -U "$POSTGRES_USER" -d "$DB_ODOO" -t -c "SELECT count(*) FROM odoo_connections;" | xargs)"
        echo "Odoo Mappings: $(docker compose exec -T postgres psql -U "$POSTGRES_USER" -d "$DB_ODOO" -t -c "SELECT count(*) FROM inventory_mappings;" | xargs)"
    else
        echo "Postgres container is not running. Cannot fetch DB stats."
    fi
    echo ""
}

run_doctor() {
    echo "=== Circles.Market Doctor ==="
    echo ""
    local ERRORS=0

    echo -n "[check] Docker: "
    if command -v docker >/dev/null 2>&1; then echo "OK"; else echo "MISSING"; ((ERRORS++)); fi

    echo -n "[check] Docker Compose: "
    if docker compose version >/dev/null 2>&1; then echo "OK"; else echo "MISSING"; ((ERRORS++)); fi

    echo -n "[check] OpenSSL: "
    if command -v openssl >/dev/null 2>&1; then echo "OK"; else echo "MISSING"; ((ERRORS++)); fi

    echo -n "[check] Curl: "
    if command -v curl >/dev/null 2>&1; then echo "OK"; else echo "MISSING"; ((ERRORS++)); fi

    echo -n "[check] Compose Config: "
    if docker compose config >/dev/null 2>&1; then echo "OK"; else echo "FAIL"; ((ERRORS++)); fi

    echo -n "[check] RPC URL: "
    if [ -n "$RPC" ]; then echo "SET ($RPC)"; else echo "NOT SET (using default)"; fi

    echo ""
    if [ $ERRORS -eq 0 ]; then
        echo "Doctor found no issues."
    else
        echo "Doctor found $ERRORS issues. Please check your environment."
        exit 1
    fi
}

CMD="$1"
shift

case "$CMD" in
    auth-codedisp)    "$(dirname "$0")/setup-market-codedisp-auth.sh" "$@" ;;
    mapping-codedisp) "$(dirname "$0")/set-codedisp-mapping.sh" "$@" ;;
    seed-pool)        "$(dirname "$0")/seed-code-pool.sh" "$@" ;;
    auth-odoo)        "$(dirname "$0")/setup-market-odoo-auth.sh" "$@" ;;
    odoo-connection)  "$(dirname "$0")/set-odoo-connection.sh" "$@" ;;
    odoo-mapping)     "$(dirname "$0")/set-odoo-inventory-mapping.sh" "$@" ;;
    psql)             "$(dirname "$0")/psql.sh" "$@" ;;
    show)             "$(dirname "$0")/show-config.sh" "$@" ;;
    status)           show_status ;;
    doctor)           run_doctor ;;
    *)                die "Unknown command: $CMD. Run with --help for usage." ;;
esac
