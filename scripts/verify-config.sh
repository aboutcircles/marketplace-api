#!/bin/bash
set -e
source "$(dirname "$0")/_lib.sh"

echo "=== Circles.Market UX Verification ==="

# 1. Check .env file
if [ ! -f .env ]; then
    echo "[verify] .env not found. Creating from .env.example..."
    cp .env.example .env
fi

# 2. Check if docker-compose works
echo "[verify] Checking docker compose configuration..."
docker compose config > /dev/null

# 3. Check ops.sh help
echo "[verify] Checking scripts/ops.sh --help..."
"$(dirname "$0")/ops.sh" --help > /dev/null || die "ops.sh --help failed"

# 4. Check psql.sh help
echo "[verify] Checking scripts/psql.sh --help..."
./scripts/psql.sh --help > /dev/null || die "psql.sh --help failed"

# 5. Check script failure mode (missing args)
echo "[verify] Checking script failure behavior (missing args)..."
set +e
./scripts/seed-code-pool.sh > /dev/null 2>&1
CODE1=$?
./scripts/set-codedisp-mapping.sh > /dev/null 2>&1
CODE2=$?
./scripts/set-odoo-connection.sh > /dev/null 2>&1
CODE3=$?
./scripts/ops.sh unknown_command > /dev/null 2>&1
CODE4=$?
set -e
if [ $CODE1 -ne 2 ] || [ $CODE2 -ne 2 ] || [ $CODE3 -ne 2 ] || [ $CODE4 -ne 2 ]; then
    die "Scripts should exit with 2 when missing/bad args. Got: $CODE1, $CODE2, $CODE3, $CODE4"
fi
echo "[verify] Script failure behavior is correct."

# 6. Check odoo connection script help mentions correct columns
echo "[verify] Checking set-odoo-connection.sh help output..."
./scripts/set-odoo-connection.sh --help 2>&1 | grep -q "odoo_url" || die "Help should mention odoo_url"
./scripts/set-odoo-connection.sh --help 2>&1 | grep -q "odoo_db" || die "Help should mention odoo_db"
./scripts/set-odoo-connection.sh --help 2>&1 | grep -q "odoo_uid" || die "Help should mention odoo_uid"
./scripts/set-odoo-connection.sh --help 2>&1 | grep -q "odoo_key" || die "Help should mention odoo_key"

# 7. Check docker-compose.yml for stale odoo profile
echo "[verify] Checking docker-compose.yml for stale odoo profile..."
grep -q "profiles:.*odoo" docker-compose.yml && die "docker-compose.yml still contains odoo profile"

# 8. Check docker-compose.yml for correct RPC URL
echo "[verify] Checking docker-compose.yml for correct RPC URL..."
grep -q "RPC: https://rpc.aboutcircles.com/" docker-compose.yml || die "docker-compose.yml has incorrect RPC URL"

# 10. Task-specific regression checks (adapter ports and flows)
echo "[verify] Running regression checks for adapter ports..."
grep -q "5680:5680" docker-compose.yml || die "docker-compose.yml should map CodeDispenser to 5680"
grep -q "5678:5678" docker-compose.yml || die "docker-compose.yml should map Odoo to 5678"

echo "[verify] Checking docs for stale 18080 references..."
grep -r "18080" docs/quickstart.md | grep -q "adapter" && die "docs/quickstart.md still mentions 18080 for adapters"

echo "[verify] Verifying ops.md flow headings..."
grep -q "Flow: Bring a CodeDispenser voucher offer live" docs/ops.md || die "ops.md missing CodeDispenser flow heading"
grep -q "Flow: Bring an Odoo-backed offer live" docs/ops.md || die "ops.md missing Odoo flow heading"

# 9. Check health endpoints (only if containers are up)
echo "[verify] Checking health endpoints (optional)..."
if docker compose ps | grep -q "Up"; then
    echo "[verify] Containers are up, checking health..."
    curl -s --fail http://localhost:5084/health > /dev/null || echo "[verify] Warning: Market API health check failed (is it still starting?)"
    curl -s --fail http://localhost:5680/health > /dev/null || echo "[verify] Warning: CodeDispenser health check failed"
    curl -s --fail http://localhost:5678/health > /dev/null || echo "[verify] Warning: Odoo health check failed"
else
    echo "[verify] Containers are not running, skipping live health checks."
fi

echo "=== Verification Successful ==="
