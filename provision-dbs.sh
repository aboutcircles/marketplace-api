#!/usr/bin/env sh
set -eu

# Configuration with defaults
: "${POSTGRES_HOST:=postgres}"
: "${POSTGRES_PORT:=5432}"
: "${POSTGRES_USER:=postgres}"
: "${POSTGRES_PASSWORD:=}"
: "${DB_MARKET_API:=circles_market_api}"
: "${DB_CODEDISP:=circles_codedisp}"
: "${DB_ODOO:=circles_odoo}"

provision_db() {
  DB_NAME=$1
  echo "[provision-dbs] Ensuring database '$DB_NAME' exists..."

  # Check if database exists (idempotent)
  EXISTS=$(PGPASSWORD="$POSTGRES_PASSWORD" psql -h "$POSTGRES_HOST" -p "$POSTGRES_PORT" -U "$POSTGRES_USER" -d postgres -At -c "SELECT 1 FROM pg_database WHERE datname = '$DB_NAME'" 2>/dev/null || echo "")

  if [ "$EXISTS" != "1" ]; then
    echo "[provision-dbs] Creating database '$DB_NAME'..."
    PGPASSWORD="$POSTGRES_PASSWORD" psql -h "$POSTGRES_HOST" -p "$POSTGRES_PORT" -U "$POSTGRES_USER" -d postgres -c "CREATE DATABASE \"$DB_NAME\""
  else
    echo "[provision-dbs] Database '$DB_NAME' already exists."
  fi
}

provision_db "$DB_MARKET_API"
provision_db "$DB_CODEDISP"
provision_db "$DB_ODOO"

echo "[provision-dbs] All databases provisioned."
