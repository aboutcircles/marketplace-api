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

# ── 4. Compute SafeMessage hash and sign it ──────────────────────────
# Safe's isValidSignature(bytes, bytes) internally wraps the message:
#   1. keccak256(rawBytes) → messageHash
#   2. keccak256(abi.encode(SAFE_MSG_TYPEHASH, messageHash)) → structHash
#   3. keccak256(0x19 || 0x01 || domainSeparator || structHash) → safeMessageHash
# The signature must be over safeMessageHash, NOT the raw SIWE message.
# cast wallet sign --no-hash signs the raw hash without EIP-191 prefix.
echo "── Step 2: Compute SafeMessage hash & sign"

# Get Safe's domain separator on-chain
DOMAIN_SEP=$(cast call "$SAFE_ADDRESS" "domainSeparator()(bytes32)" --rpc-url "$RPC_URL" 2>/dev/null)
echo "   Domain separator: ${DOMAIN_SEP:0:18}..."

# SAFE_MSG_TYPEHASH = keccak256("SafeMessage(bytes message)")
SAFE_MSG_TYPEHASH=$(cast keccak "SafeMessage(bytes message)" 2>/dev/null)

# Hash the raw SIWE message bytes
SIWE_HEX=$(printf '%s' "$SIWE_MESSAGE" | xxd -p | tr -d '\n')
MSG_HASH=$(cast keccak "0x${SIWE_HEX}" 2>/dev/null)

# structHash = keccak256(abi.encode(SAFE_MSG_TYPEHASH, msgHash))
STRUCT_ENCODED=$(cast abi-encode "f(bytes32,bytes32)" "$SAFE_MSG_TYPEHASH" "$MSG_HASH" 2>/dev/null)
STRUCT_HASH=$(cast keccak "$STRUCT_ENCODED" 2>/dev/null)

# EIP-712 final hash = keccak256(0x19 || 0x01 || domainSeparator || structHash)
PACKED=$(cast concat-hex "0x1901" "$DOMAIN_SEP" "$STRUCT_HASH" 2>/dev/null)
SAFE_MSG_HASH=$(cast keccak "$PACKED" 2>/dev/null)
echo "   SafeMessage hash: ${SAFE_MSG_HASH:0:18}..."

# Sign the SafeMessage hash directly (no EIP-191 wrapping)
SIGNATURE=$(cast wallet sign --no-hash --private-key "$PRIVATE_KEY" "$SAFE_MSG_HASH" 2>/dev/null)
echo "   Signature: ${SIGNATURE:0:20}...${SIGNATURE: -8}"
echo "   (signed SafeMessage hash, not raw SIWE message)"
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
