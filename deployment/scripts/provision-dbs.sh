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

: "${DB_MARKET_API_USER:=market_api}"
: "${DB_CODEDISP_USER:=codedisp}"
: "${DB_ODOO_USER:=odoo}"

: "${DB_MARKET_API_PASSWORD:=}"
: "${DB_CODEDISP_PASSWORD:=}"
: "${DB_ODOO_PASSWORD:=}"

psql_admin() {
    PGPASSWORD="$POSTGRES_PASSWORD" psql -h "$POSTGRES_HOST" -p "$POSTGRES_PORT" -U "$POSTGRES_USER" -d postgres "$@"
}

provision_service() {
    DB_NAME=$1
    DB_USER=$2
    DB_PASS=$3

    if [ -z "$DB_PASS" ]; then
        echo "[provision-dbs] ERROR: No password provided for user '$DB_USER'"
        exit 1
    fi

    echo "[provision-dbs] Ensuring role '$DB_USER' exists..."
    ROLE_EXISTS=$(psql_admin -At -c "SELECT 1 FROM pg_roles WHERE rolname = '$DB_USER'" 2>/dev/null || echo "")
    if [ "$ROLE_EXISTS" != "1" ]; then
        psql_admin -c "CREATE ROLE \"$DB_USER\" WITH LOGIN PASSWORD '$DB_PASS'"
    else
        echo "[provision-dbs] Role '$DB_USER' already exists. Updating password..."
        psql_admin -c "ALTER ROLE \"$DB_USER\" WITH PASSWORD '$DB_PASS'"
    fi

    echo "[provision-dbs] Ensuring database '$DB_NAME' exists and is owned by '$DB_USER'..."
    DB_EXISTS=$(psql_admin -At -c "SELECT 1 FROM pg_database WHERE datname = '$DB_NAME'" 2>/dev/null || echo "")
    if [ "$DB_EXISTS" != "1" ]; then
        psql_admin -c "CREATE DATABASE \"$DB_NAME\" OWNER \"$DB_USER\""
    else
        echo "[provision-dbs] Database '$DB_NAME' already exists. Ensuring ownership..."
        psql_admin -c "ALTER DATABASE \"$DB_NAME\" OWNER TO \"$DB_USER\""
    fi

    # Revoke public schema privileges and grant them to the service user (standard hardening)
    psql_admin -d "$DB_NAME" -c "REVOKE ALL ON SCHEMA public FROM PUBLIC; GRANT ALL ON SCHEMA public TO \"$DB_USER\""
}

echo "[provision-dbs] Starting DB provisioning..."

# Wait for postgres to be ready
until psql_admin -c '\q' >/dev/null 2>&1; do
  echo "[provision-dbs] Waiting for PostgreSQL to be ready..."
  sleep 1
done

provision_service "$DB_MARKET_API" "$DB_MARKET_API_USER" "$DB_MARKET_API_PASSWORD"
provision_service "$DB_CODEDISP" "$DB_CODEDISP_USER" "$DB_CODEDISP_PASSWORD"
provision_service "$DB_ODOO" "$DB_ODOO_USER" "$DB_ODOO_PASSWORD"

echo "[provision-dbs] All services provisioned successfully."
