#!/usr/bin/env bash
set -euo pipefail
#
# End-to-end integration test for the WooCommerce fulfillment adapter.
# Prerequisites: the full WC dev stack must be running:
#   make up-wc    (or docker compose -f ... -f ... up -d --build)
#
# Exercises: health → DB config → inventory → availability → fulfill → idempotency

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

# Load deployment .env if env vars aren't already set
if [ -z "${CIRCLES_SERVICE_KEY:-}" ] && [ -f "${REPO_DIR}/deployment/.env" ]; then
  set -a
  # shellcheck disable=SC1091
  . "${REPO_DIR}/deployment/.env"
  set +a
fi

# ── Ports ────────────────────────────────────────────────────────────────────

WC_ADAPTER_PORT="${MARKET_WOOCOMMERCE_ADAPTER_PORT:-65010}"
WP_PORT="${WORDPRESS_PORT:-65012}"

# ── Auth ─────────────────────────────────────────────────────────────────────

SERVICE_KEY="${CIRCLES_SERVICE_KEY:?CIRCLES_SERVICE_KEY required}"
POSTGRES_USER="${POSTGRES_USER:-postgres}"
DB_WC="${DB_WOOCOMMERCE:-circles_woocommerce}"

# ── Test constants ───────────────────────────────────────────────────────────

CHAIN_ID=100
SELLER="0x0000000000000000000000000000000000000001"
MARKETPLACE_SKU="test-tshirt"
WC_PRODUCT_SKU="circles-test-tshirt"

# ── Compose helper ───────────────────────────────────────────────────────────

DC="docker compose --project-directory ${REPO_DIR}/deployment \
  -f ${REPO_DIR}/deployment/docker-compose.dev.yml \
  -f ${REPO_DIR}/deployment/docker-compose.woocommerce.yml"

psql_wc() {
  $DC exec -T postgres psql -U "$POSTGRES_USER" -d "$DB_WC" "$@"
}

# ── Helpers ──────────────────────────────────────────────────────────────────

ok()   { printf "\033[32m[OK]\033[0m %s\n" "$*"; }
fail() { printf "\033[31m[FAIL]\033[0m %s\n" "$*" >&2; exit 1; }
info() { printf "\033[34m[INFO]\033[0m %s\n" "$*"; }

# ── Step 0: Read WC credentials ─────────────────────────────────────────────

echo "=== Step 0: Read WooCommerce credentials ==="

WC_CREDS=$($DC exec -T wc-wordpress cat /var/www/html/wc-test-credentials.json 2>/dev/null || echo "")
if [ -z "$WC_CREDS" ] || ! echo "$WC_CREDS" | python3 -c "import sys,json; json.load(sys.stdin)" 2>/dev/null; then
  fail "Could not read WC credentials from wc-wordpress container. Is wc-setup complete?"
fi
CK=$(echo "$WC_CREDS" | python3 -c "import sys,json; print(json.load(sys.stdin)['consumer_key'])")
CS=$(echo "$WC_CREDS" | python3 -c "import sys,json; print(json.load(sys.stdin)['consumer_secret'])")
ok "WC credentials loaded (CK=${CK:0:12}...)"

# ── Step 1: Health checks ───────────────────────────────────────────────────

echo
echo "=== Step 1: Health checks ==="

ADAPTER_HEALTH=$(curl -fsS "http://localhost:${WC_ADAPTER_PORT}/health" 2>/dev/null || echo "")
echo "$ADAPTER_HEALTH" | grep -q '"ok":true' || fail "WC adapter /health failed: $ADAPTER_HEALTH"
ok "WC adapter /health"

curl -fsS "http://localhost:${WP_PORT}/wp-login.php" >/dev/null 2>&1 || fail "WordPress not reachable at port ${WP_PORT}"
ok "WordPress reachable"

# ── Step 2: Register WC connection (direct DB insert) ───────────────────────

echo
echo "=== Step 2: Register WooCommerce connection via DB ==="

psql_wc <<SQL
INSERT INTO wc_connections (
  chain_id, seller_address, wc_base_url, wc_consumer_key, wc_consumer_secret,
  order_status, timeout_ms, fulfill_inherit_request_abort, enabled, revoked_at
) VALUES (
  ${CHAIN_ID}, '${SELLER}', 'http://wc-wordpress', '${CK}', '${CS}',
  'processing', 30000, true, true, NULL
)
ON CONFLICT (chain_id, seller_address) WHERE revoked_at IS NULL DO UPDATE SET
  wc_base_url = EXCLUDED.wc_base_url,
  wc_consumer_key = EXCLUDED.wc_consumer_key,
  wc_consumer_secret = EXCLUDED.wc_consumer_secret,
  order_status = EXCLUDED.order_status,
  timeout_ms = EXCLUDED.timeout_ms,
  enabled = true,
  revoked_at = NULL;
SQL
ok "Connection registered for seller ${SELLER:0:10}..."

# ── Step 3: Create product mapping (direct DB insert) ───────────────────────

echo
echo "=== Step 3: Create product mapping via DB ==="

psql_wc <<SQL
INSERT INTO wc_product_mappings (
  chain_id, seller_address, sku, wc_product_sku, enabled, revoked_at
) VALUES (
  ${CHAIN_ID}, '${SELLER}', '${MARKETPLACE_SKU}', '${WC_PRODUCT_SKU}', true, NULL
)
ON CONFLICT (chain_id, seller_address, sku) WHERE revoked_at IS NULL DO UPDATE SET
  wc_product_sku = EXCLUDED.wc_product_sku,
  enabled = true,
  revoked_at = NULL;
SQL
ok "Product mapping: ${MARKETPLACE_SKU} → ${WC_PRODUCT_SKU}"

# ── Step 4: Test /inventory ─────────────────────────────────────────────────

echo
echo "=== Step 4: Test /inventory ==="

INV_RESULT=$(curl -fsS \
  -H "X-Circles-Service-Key: ${SERVICE_KEY}" \
  "http://localhost:${WC_ADAPTER_PORT}/inventory/${CHAIN_ID}/${SELLER}/${MARKETPLACE_SKU}" 2>&1)
echo "$INV_RESULT" | grep -q '"value"' || fail "Inventory check failed: $INV_RESULT"
ok "Inventory: $INV_RESULT"

# ── Step 5: Test /availability ──────────────────────────────────────────────

echo
echo "=== Step 5: Test /availability ==="

AVAIL_RESULT=$(curl -fsS \
  -H "X-Circles-Service-Key: ${SERVICE_KEY}" \
  "http://localhost:${WC_ADAPTER_PORT}/availability/${CHAIN_ID}/${SELLER}/${MARKETPLACE_SKU}" 2>&1)
echo "$AVAIL_RESULT" | grep -q 'InStock' || fail "Availability check failed: $AVAIL_RESULT"
ok "Availability: InStock"

# ── Step 6: Test /fulfill ───────────────────────────────────────────────────

echo
echo "=== Step 6: Test /fulfill ==="

PAYMENT_REF="test-payment-$(date +%s)"
FULFILL_RESULT=$(curl -fsS -X POST \
  -H "X-Circles-Service-Key: ${SERVICE_KEY}" \
  -H "Content-Type: application/json" \
  -d "{
    \"orderId\": \"test-order-001\",
    \"paymentReference\": \"${PAYMENT_REF}\",
    \"buyer\": \"0x0000000000000000000000000000000000000002\",
    \"currency\": \"EUR\",
    \"contactPoint\": { \"email\": \"buyer@test.local\" },
    \"customer\": { \"givenName\": \"Test\", \"familyName\": \"Buyer\" },
    \"shippingAddress\": {
      \"givenName\": \"Test\",
      \"familyName\": \"Buyer\",
      \"streetAddress\": \"123 Test St\",
      \"addressLocality\": \"Berlin\",
      \"postalCode\": \"10115\",
      \"addressCountry\": \"DE\"
    },
    \"items\": [
      { \"sku\": \"${MARKETPLACE_SKU}\", \"quantity\": 1 }
    ]
  }" \
  "http://localhost:${WC_ADAPTER_PORT}/fulfill/${CHAIN_ID}/${SELLER}" 2>&1)
echo "$FULFILL_RESULT" | grep -q '"status":"ok"' || fail "Fulfillment failed: $FULFILL_RESULT"
ok "Fulfillment succeeded"
info "$FULFILL_RESULT"

# ── Step 7: Verify idempotency ──────────────────────────────────────────────

echo
echo "=== Step 7: Verify idempotent replay ==="

FULFILL_AGAIN=$(curl -fsS -X POST \
  -H "X-Circles-Service-Key: ${SERVICE_KEY}" \
  -H "Content-Type: application/json" \
  -d "{
    \"orderId\": \"test-order-001\",
    \"paymentReference\": \"${PAYMENT_REF}\",
    \"buyer\": \"0x0000000000000000000000000000000000000002\",
    \"items\": [{ \"sku\": \"${MARKETPLACE_SKU}\", \"quantity\": 1 }]
  }" \
  "http://localhost:${WC_ADAPTER_PORT}/fulfill/${CHAIN_ID}/${SELLER}" 2>&1)
echo "$FULFILL_AGAIN" | grep -q '"status":"ok"' || fail "Idempotency failed: $FULFILL_AGAIN"
ok "Idempotent replay returned ok"

# ── Step 8: Verify fulfillment run in DB ────────────────────────────────────

echo
echo "=== Step 8: Verify fulfillment run in DB ==="

RUN_COUNT=$(psql_wc -At -c "SELECT count(*) FROM wc_fulfillment_runs WHERE payment_reference = '${PAYMENT_REF}'")
[ "$RUN_COUNT" -ge 1 ] || fail "Expected at least 1 fulfillment run, got: $RUN_COUNT"
ok "Fulfillment run recorded (count=$RUN_COUNT)"

RUN_STATUS=$(psql_wc -At -c "SELECT status FROM wc_fulfillment_runs WHERE payment_reference = '${PAYMENT_REF}' LIMIT 1")
[ "$RUN_STATUS" = "completed" ] || fail "Expected run status 'completed', got: $RUN_STATUS"
ok "Fulfillment run status: completed"

WC_ORDER_ID=$(psql_wc -At -c "SELECT wc_order_id FROM wc_fulfillment_runs WHERE payment_reference = '${PAYMENT_REF}' LIMIT 1")
[ -n "$WC_ORDER_ID" ] && [ "$WC_ORDER_ID" != "" ] || fail "No WC order ID recorded"
ok "WC order ID: $WC_ORDER_ID"

# ── Done ─────────────────────────────────────────────────────────────────────

echo
echo "══════════════════════════════════════════════════"
ok "All WooCommerce integration tests passed!"
echo "══════════════════════════════════════════════════"
