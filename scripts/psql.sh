#!/bin/bash
set -e

help_requested() {
  for arg in "$@"; do
    case "$arg" in
      -h|--help) return 0 ;;
    esac
  done
  return 1
}

print_usage() {
  local name="$1"
  local usage="$2"
  local exit_code="${3:-1}"
  echo "Usage: $name $usage"
  exit "$exit_code"
}

die() {
  echo "Error: $1" >&2
  exit 1
}

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
