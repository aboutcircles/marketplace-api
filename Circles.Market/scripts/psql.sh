#!/bin/bash
set -e
source "$(dirname "$0")/_lib.sh"

USAGE="<db_alias> [psql_args...]

Aliases:
  market    -> ${DB_MARKET_API:-circles_market_api}
  codedisp  -> ${DB_CODEDISP:-circles_codedisp}
  odoo      -> ${DB_ODOO:-circles_odoo}

Example:
  $0 odoo -c 'SELECT * FROM odoo_connections;'"

if help_requested "$@"; then
    print_usage "$(basename "$0")" "$USAGE" 0
fi

if [ $# -lt 1 ]; then
    print_usage "$(basename "$0")" "$USAGE"
fi

DB_ALIAS="$1"
shift
POSTGRES_USER="${POSTGRES_USER:-postgres}"

case "$DB_ALIAS" in
  market)
    DB_NAME="${DB_MARKET_API:-circles_market_api}"
    ;;
  codedisp)
    DB_NAME="${DB_CODEDISP:-circles_codedisp}"
    ;;
  odoo)
    DB_NAME="${DB_ODOO:-circles_odoo}"
    ;;
  *)
    die "Unknown database alias: $DB_ALIAS. Use 'market', 'codedisp', or 'odoo'."
    ;;
esac

echo "[psql] Connecting to $DB_NAME as $POSTGRES_USER..."
docker compose exec -it postgres psql -U "$POSTGRES_USER" -d "$DB_NAME" "$@"
