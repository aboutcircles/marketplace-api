#!/usr/bin/env bash
# test-auth-flow.sh — End-to-end SIWE auth test against staging
# Requires: cast (foundry), curl, jq
# Uses SEED_PHRASE from environment (via .envrc / direnv)
set -euo pipefail

STAGING_URL="${STAGING_URL:-https://staging.circlesubi.network/market}"
CHAIN_ID="${CHAIN_ID:-100}"

# ── 1. Derive wallet from seed phrase ────────────────────────────────
if [ -z "${SEED_PHRASE:-}" ]; then
  echo "ERROR: SEED_PHRASE not set. Run 'direnv allow' or export it manually."
  exit 1
fi

PRIVATE_KEY=$(cast wallet derive-private-key "$SEED_PHRASE" 0 2>/dev/null | command grep -oE '0x[0-9a-fA-F]{64}')
ADDRESS=$(cast wallet address "$PRIVATE_KEY" 2>/dev/null)

echo "=== SIWE Auth Flow Test ==="
echo "Staging:  $STAGING_URL"
echo "Address:  $ADDRESS"
echo "Chain ID: $CHAIN_ID"
echo ""

# ── 2. Request challenge ─────────────────────────────────────────────
echo "── Step 1: POST /api/auth/challenge"
CHALLENGE_RESPONSE=$(curl -s -w "\n%{http_code}" -X POST \
  "${STAGING_URL}/api/auth/challenge" \
  -H 'Content-Type: application/json' \
  -d "{\"address\":\"${ADDRESS}\",\"chainId\":${CHAIN_ID}}")

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

# ── 3. Sign SIWE message (EIP-191 personal_sign) ────────────────────
echo "── Step 2: Sign SIWE message"
SIGNATURE=$(cast wallet sign --private-key "$PRIVATE_KEY" "$SIWE_MESSAGE" 2>/dev/null)
echo "   Signature: ${SIGNATURE:0:20}...${SIGNATURE: -8}"
echo ""

# ── 4. Verify signature ─────────────────────────────────────────────
echo "── Step 3: POST /api/auth/verify"
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

  # Decode JWT payload (base64url → json)
  PAYLOAD=$(echo "$TOKEN" | cut -d. -f2 | tr '_-' '/+' | base64 -d 2>/dev/null || true)
  if [ -n "$PAYLOAD" ]; then
    echo "── JWT Claims:"
    echo "$PAYLOAD" | jq . 2>/dev/null || echo "   $PAYLOAD"
  fi

  echo ""
  echo "── Step 4: Test authenticated endpoint"
  SELLERS_RESPONSE=$(curl -s -w "\n%{http_code}" \
    "${STAGING_URL}/api/cart/v1/orders" \
    -H "Authorization: Bearer ${TOKEN}")
  HTTP_CODE_AUTH=$(echo "$SELLERS_RESPONSE" | tail -1)
  AUTH_BODY=$(echo "$SELLERS_RESPONSE" | sed '$d')
  echo "   GET /api/cart/v1/orders → HTTP $HTTP_CODE_AUTH"
  echo "   Body: $(echo "$AUTH_BODY" | head -1 | cut -c1-120)"

  echo ""
  echo "=== AUTH FLOW: SUCCESS ==="
else
  echo "   FAILED: $VERIFY_BODY"
  echo ""
  echo "=== AUTH FLOW: FAILED at verify ==="
  exit 1
fi
