#!/bin/bash
set -e
source "$(dirname "$0")/_lib.sh"

USAGE="<pool_id> <code_or_file> [--file]

Example:
  $0 my-pool \"SECRET123\"
  $0 my-pool codes.txt --file"

if help_requested "$@"; then
    print_usage "$(basename "$0")" "$USAGE" 0
fi

if [ $# -lt 2 ]; then
    print_usage "$(basename "$0")" "$USAGE"
fi

POOL_ID="$1"
INPUT="$2"
IS_FILE=false

if [[ "${3:-}" == "--file" ]] || [[ "${4:-}" == "--file" ]]; then
    IS_FILE=true
fi

if [ -z "$POOL_ID" ]; then
    die "pool_id cannot be empty"
fi

if [ -z "$INPUT" ]; then
    die "code_or_file cannot be empty"
fi

POSTGRES_USER="${POSTGRES_USER:-postgres}"
DB_CODEDISP="${DB_CODEDISP:-circles_codedisp}"

echo "[seed-pool] Ensuring pool '$POOL_ID' exists..."
docker compose exec -T postgres psql -U "$POSTGRES_USER" -d "$DB_CODEDISP" -c "INSERT INTO code_pools(pool_id) VALUES ('$POOL_ID') ON CONFLICT DO NOTHING;"

if [ "$IS_FILE" = true ]; then
    if [ ! -f "$INPUT" ]; then
        die "File not found: $INPUT"
    fi
    echo "[seed-pool] Seeding codes from file '$INPUT' into pool '$POOL_ID'..."
    # Read file line by line and insert
    while IFS= read -r line || [ -n "$line" ]; do
        # skip empty lines
        [[ -z "$line" ]] && continue
        docker compose exec -T postgres psql -U "$POSTGRES_USER" -d "$DB_CODEDISP" -c "INSERT INTO code_pool_codes (pool_id, code) VALUES ('$POOL_ID', '$line') ON CONFLICT DO NOTHING;"
    done < "$INPUT"
else
    echo "[seed-pool] Seeding code '$INPUT' into pool '$POOL_ID'..."
    docker compose exec -T postgres psql -U "$POSTGRES_USER" -d "$DB_CODEDISP" -c "INSERT INTO code_pool_codes (pool_id, code) VALUES ('$POOL_ID', '$INPUT') ON CONFLICT DO NOTHING;"
fi

echo "[seed-pool] Done."
