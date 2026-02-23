#!/usr/bin/env bash
# test-safe-auth-flow.sh — End-to-end SIWE auth test for Safe (ERC-1271) against staging
# Requires: cast (foundry), curl, jq
# Uses SEED_PHRASE from environment (via .envrc / direnv)
#
# Flow:
#   1. Derive EOA from SEED_PHRASE
#   2. Request SIWE challenge for the SAFE address (not the EOA)
#   3. Sign with EOA key → ecrecover won't match Safe → falls back to ERC-1271
#   4. Server calls isValidSignature on-chain → Safe verifies EOA is an owner
#   5. JWT issued for the Safe address
set -euo pipefail

STAGING_URL="${STAGING_URL:-https://staging.circlesubi.network/market}"
CHAIN_ID="${CHAIN_ID:-100}"
SAFE_ADDRESS="${SAFE_ADDRESS:-0x227642eBD3a801E7b44A5bb956c02C2d97Ca71F0}"

# ── 1. Derive wallet from seed phrase ────────────────────────────────
if [ -z "${SEED_PHRASE:-}" ]; then
  echo "ERROR: SEED_PHRASE not set. Run 'direnv allow' or export it manually."
  exit 1
fi

PRIVATE_KEY=$(cast wallet derive-private-key "$SEED_PHRASE" 0 2>/dev/null | command grep -oE '0x[0-9a-fA-F]{64}')
EOA_ADDRESS=$(cast wallet address "$PRIVATE_KEY" 2>/dev/null)

echo "=== Safe (ERC-1271) Auth Flow Test ==="
echo "Staging:      $STAGING_URL"
echo "Safe Address: $SAFE_ADDRESS"
echo "EOA (signer): $EOA_ADDRESS"
echo "Chain ID:     $CHAIN_ID"
echo ""

# ── 2. Verify Safe ownership on-chain (optional, informational) ─────
echo "── Pre-check: Verify EOA is Safe owner"
RPC_URL="${RPC_URL:-https://rpc.aboutcircles.com}"
IS_OWNER=$(cast call "$SAFE_ADDRESS" "isOwner(address)(bool)" "$EOA_ADDRESS" --rpc-url "$RPC_URL" 2>/dev/null || echo "failed")
if [ "$IS_OWNER" = "true" ]; then
  echo "   ✓ $EOA_ADDRESS is owner of Safe $SAFE_ADDRESS"
elif [ "$IS_OWNER" = "false" ]; then
  echo "   ✗ $EOA_ADDRESS is NOT an owner of Safe $SAFE_ADDRESS"
  echo "   ERC-1271 verification will fail."
  exit 1
else
  echo "   ⚠ Could not check ownership (RPC error) — proceeding anyway"
fi
echo ""

# ── 3. Request challenge for the SAFE address ───────────────────────
echo "── Step 1: POST /api/auth/challenge (for Safe address)"
CHALLENGE_RESPONSE=$(curl -s -w "\n%{http_code}" -X POST \
  "${STAGING_URL}/api/auth/challenge" \
  -H 'Content-Type: application/json' \
  -d "{\"address\":\"${SAFE_ADDRESS}\",\"chainId\":${CHAIN_ID}}")

HTTP_CODE=$(echo "$CHALLENGE_RESPONSE" | tail -1)
CHALLENGE_BODY=$(echo "$CHALLENGE_RESPONSE" | sed '$d')

echo "   HTTP $HTTP_CODE"
if [ "$HTTP_CODE" != "200" ]; then
  echo "   FAILED: $CHALLENGE_BODY"
  exit 1
fi

CHALLENGE_ID=$(echo "$CHALLENGE_BODY" | jq -r '.challengeId')
SIWE_MESSAGE=$(echo "$CHALLENGE_BODY" | jq -r '.message')
NONCE=$(echo "$CHALLENGE_BODY" | jq -r '.nonce')
EXPIRES_AT=$(echo "$CHALLENGE_BODY" | jq -r '.expiresAt')

echo "   Challenge ID: $CHALLENGE_ID"
echo "   Nonce:        $NONCE"
echo "   Expires:      $EXPIRES_AT"
echo "   Message (first 80 chars): $(echo "$SIWE_MESSAGE" | head -1 | cut -c1-80)"
echo ""

# ── 4. Sign SIWE message with EOA key (NOT the Safe) ────────────────
echo "── Step 2: Sign SIWE message with EOA key"
echo "   (ecrecover will return $EOA_ADDRESS, not $SAFE_ADDRESS)"
echo "   → server falls back to ERC-1271 isValidSignature on Safe contract"
SIGNATURE=$(cast wallet sign --private-key "$PRIVATE_KEY" "$SIWE_MESSAGE" 2>/dev/null)
echo "   Signature: ${SIGNATURE:0:20}...${SIGNATURE: -8}"
echo ""

# ── 5. Verify signature (triggers ERC-1271 on server) ───────────────
echo "── Step 3: POST /api/auth/verify (ERC-1271 path)"
VERIFY_RESPONSE=$(curl -s -w "\n%{http_code}" -X POST \
  "${STAGING_URL}/api/auth/verify" \
  -H 'Content-Type: application/json' \
  -d "{\"challengeId\":\"${CHALLENGE_ID}\",\"signature\":\"${SIGNATURE}\"}")

HTTP_CODE=$(echo "$VERIFY_RESPONSE" | tail -1)
VERIFY_BODY=$(echo "$VERIFY_RESPONSE" | sed '$d')

echo "   HTTP $HTTP_CODE"
if [ "$HTTP_CODE" = "200" ]; then
  TOKEN=$(echo "$VERIFY_BODY" | jq -r '.token')
  RETURNED_ADDR=$(echo "$VERIFY_BODY" | jq -r '.address')
  EXPIRES_IN=$(echo "$VERIFY_BODY" | jq -r '.expiresIn')

  echo "   Address:    $RETURNED_ADDR"
  echo "   Expires in: ${EXPIRES_IN}s"
  echo "   Token:      ${TOKEN:0:30}...${TOKEN: -10}"
  echo ""

  # Verify returned address is the Safe, not the EOA
  SAFE_LOWER=$(echo "$SAFE_ADDRESS" | tr '[:upper:]' '[:lower:]')
  RETURNED_LOWER=$(echo "$RETURNED_ADDR" | tr '[:upper:]' '[:lower:]')
  if [ "$SAFE_LOWER" = "$RETURNED_LOWER" ]; then
    echo "   ✓ JWT issued for Safe address (correct)"
  else
    echo "   ✗ JWT issued for $RETURNED_ADDR (expected $SAFE_ADDRESS)"
  fi

  # Decode JWT payload
  PAYLOAD=$(echo "$TOKEN" | cut -d. -f2 | tr '_-' '/+' | base64 -d 2>/dev/null || true)
  if [ -n "$PAYLOAD" ]; then
    echo ""
    echo "── JWT Claims:"
    echo "$PAYLOAD" | jq . 2>/dev/null || echo "   $PAYLOAD"
  fi

  echo ""
  echo "── Step 4: Test authenticated endpoint with Safe JWT"
  ORDERS_RESPONSE=$(curl -s -w "\n%{http_code}" \
    "${STAGING_URL}/api/cart/v1/orders" \
    -H "Authorization: Bearer ${TOKEN}")
  HTTP_CODE_AUTH=$(echo "$ORDERS_RESPONSE" | tail -1)
  AUTH_BODY=$(echo "$ORDERS_RESPONSE" | sed '$d')
  echo "   GET /api/cart/v1/orders → HTTP $HTTP_CODE_AUTH"
  echo "   Body: $(echo "$AUTH_BODY" | head -1 | cut -c1-120)"

  echo ""
  echo "=== SAFE AUTH (ERC-1271): SUCCESS ==="
else
  echo "   FAILED: $VERIFY_BODY"
  echo ""
  echo "=== SAFE AUTH (ERC-1271): FAILED at verify ==="
  echo ""
  echo "Common causes:"
  echo "  - EOA is not an owner of the Safe"
  echo "  - RPC endpoint unreachable from staging"
  echo "  - gas=0 bug (check market-api logs for 'Block gas limit exceeded')"
  exit 1
fi
