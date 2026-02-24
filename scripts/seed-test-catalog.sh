#!/usr/bin/env bash
# seed-test-catalog.sh — Seed a complete test product into the marketplace
# Builds the full IPFS document chain: Product → CustomDataLink → NamespaceChunk → NameIndexDoc → Profile
# Then registers the profile in the pinning service DB and creates a market route.
#
# Requires: cast (foundry), curl, jq
# Uses SEED_PHRASE from environment (via .envrc / direnv)
set -euo pipefail

STAGING_URL="${STAGING_URL:-https://staging.circlesubi.network/market}"
PINNING_URL="${PINNING_URL:-https://staging.circlesubi.network/profiles}"
CHAIN_ID="${CHAIN_ID:-100}"
SKU="${SKU:-test-mug-001}"
PRODUCT_NAME="${PRODUCT_NAME:-Test Mug}"
PRODUCT_DESC="${PRODUCT_DESC:-A test mug for staging verification}"
PRICE="${PRICE:-5.00}"
CURRENCY="${CURRENCY:-EUR}"

# ── 0. Derive wallet ─────────────────────────────────────────────────
if [ -z "${SEED_PHRASE:-}" ]; then
  echo "ERROR: SEED_PHRASE not set. Run 'direnv allow' or export it manually."
  exit 1
fi

PRIVATE_KEY=$(cast wallet derive-private-key "$SEED_PHRASE" 0 2>/dev/null | command grep -oE '0x[0-9a-fA-F]{64}')
ADDRESS=$(cast wallet address "$PRIVATE_KEY" 2>/dev/null)
ADDRESS_LOWER=$(echo "$ADDRESS" | tr '[:upper:]' '[:lower:]')

echo "=== Marketplace Test Catalog Seeder ==="
echo "Staging:  $STAGING_URL"
echo "Pinning:  $PINNING_URL"
echo "Address:  $ADDRESS ($ADDRESS_LOWER)"
echo "Chain ID: $CHAIN_ID"
echo "SKU:      $SKU"
echo ""

# Helper: POST JSON to pinning service directly (stores in cid_content for /raw/ retrieval)
# Using the pinning service's /pin endpoint (not market API's /api/pin which goes through /pin-media
# and only stores on Filebase without DB caching)
pin_json() {
  local json="$1"
  local label="$2"
  local response
  response=$(curl -s -w "\n%{http_code}" -X POST \
    "${PINNING_URL}/pin" \
    -H 'Content-Type: application/json' \
    -d "$json")
  local http_code
  http_code=$(echo "$response" | tail -1)
  local body
  body=$(echo "$response" | command sed '$d')

  if [ "$http_code" != "200" ] && [ "$http_code" != "201" ]; then
    echo "   FAILED to pin $label (HTTP $http_code): $body" >&2
    exit 1
  fi

  local cid
  cid=$(echo "$body" | jq -r '.cid // empty')
  if [ -z "$cid" ]; then
    echo "   FAILED to extract CID for $label from: $body" >&2
    exit 1
  fi
  echo "$cid"
}

# ── Step A: Pin Product JSON ─────────────────────────────────────────
echo "── Step A: Pin Product"

PRODUCT_JSON=$(jq -n \
  --arg name "$PRODUCT_NAME" \
  --arg desc "$PRODUCT_DESC" \
  --arg sku "$SKU" \
  --arg price "$PRICE" \
  --arg currency "$CURRENCY" \
  --arg seller_id "eip155:${CHAIN_ID}:${ADDRESS_LOWER}" \
  '{
    "@context": ["https://schema.org/", "https://aboutcircles.com/contexts/circles-market/"],
    "@type": "Product",
    "name": $name,
    "description": $desc,
    "sku": $sku,
    "offers": [{
      "@type": "Offer",
      "price": ($price | tonumber),
      "priceCurrency": $currency,
      "seller": {
        "@type": "Organization",
        "@id": $seller_id
      }
    }]
  }')

PRODUCT_CID=$(pin_json "$PRODUCT_JSON" "Product")
echo "   Product CID: $PRODUCT_CID"

# ── Step B: Build + Sign CustomDataLink ───────────────────────────────
echo ""
echo "── Step B: Build and sign CustomDataLink"

NONCE="0x$(openssl rand -hex 16)"
SIGNED_AT=$(date +%s)

# Build link JSON WITHOUT signature first (for canonicalization)
LINK_UNSIGNED=$(jq -n \
  --arg cid "$PRODUCT_CID" \
  --arg name "product/${SKU}" \
  --argjson chainId "$CHAIN_ID" \
  --arg signerAddress "$ADDRESS_LOWER" \
  --argjson signedAt "$SIGNED_AT" \
  --arg nonce "$NONCE" \
  '{
    "@context": "https://aboutcircles.com/contexts/circles-linking/",
    "@type": "CustomDataLink",
    "name": $name,
    "cid": $cid,
    "chainId": $chainId,
    "signerAddress": $signerAddress,
    "signedAt": $signedAt,
    "nonce": $nonce,
    "encrypted": false,
    "encryptionAlgorithm": null,
    "encryptionKeyFingerprint": null
  }')

echo "   Canonicalizing..."
CANONICAL_JSON=$(curl -s -X POST \
  "${STAGING_URL}/api/canonicalize" \
  -H 'Content-Type: application/json' \
  -d "$LINK_UNSIGNED")

if [ -z "$CANONICAL_JSON" ] || echo "$CANONICAL_JSON" | jq -e '.title == "Invalid JSON"' >/dev/null 2>&1; then
  echo "   FAILED to canonicalize: $CANONICAL_JSON" >&2
  exit 1
fi
echo "   Canonical JSON (first 100 chars): ${CANONICAL_JSON:0:100}..."

# Keccak256 hash the canonical bytes
HASH=$(echo -n "$CANONICAL_JSON" | cast keccak)
echo "   Keccak256 hash: $HASH"

# EIP-191 personal_sign the hash
SIGNATURE=$(cast wallet sign --private-key "$PRIVATE_KEY" --no-hash "$HASH" 2>/dev/null)
echo "   Signature: ${SIGNATURE:0:20}...${SIGNATURE: -8}"
echo "   Signature length: ${#SIGNATURE} chars (expect 132)"

# Build the full signed link
LINK_SIGNED=$(echo "$LINK_UNSIGNED" | jq --arg sig "$SIGNATURE" '. + {signature: $sig}')

# ── Step C: Pin NamespaceChunk ────────────────────────────────────────
echo ""
echo "── Step C: Pin NamespaceChunk"

CHUNK_JSON=$(jq -n \
  --arg cid "$PRODUCT_CID" \
  --arg name "product/${SKU}" \
  --arg signerAddress "$ADDRESS_LOWER" \
  --arg signature "$SIGNATURE" \
  --argjson chainId "$CHAIN_ID" \
  --argjson signedAt "$SIGNED_AT" \
  --arg nonce "$NONCE" \
  '{
    "@context": "https://aboutcircles.com/contexts/circles-namespace/",
    "@type": "NamespaceChunk",
    "prev": null,
    "links": [{
      "@context": "https://aboutcircles.com/contexts/circles-linking/",
      "@type": "CustomDataLink",
      "name": $name,
      "cid": $cid,
      "chainId": $chainId,
      "signerAddress": $signerAddress,
      "signedAt": $signedAt,
      "nonce": $nonce,
      "encrypted": false,
      "encryptionAlgorithm": null,
      "encryptionKeyFingerprint": null,
      "signature": $signature
    }]
  }')

CHUNK_CID=$(pin_json "$CHUNK_JSON" "NamespaceChunk")
echo "   Chunk CID: $CHUNK_CID"

# ── Step D: Pin NameIndexDoc ──────────────────────────────────────────
echo ""
echo "── Step D: Pin NameIndexDoc"

INDEX_JSON=$(jq -n \
  --arg head "$CHUNK_CID" \
  --arg entry_key "product/${SKU}" \
  --arg entry_val "$CHUNK_CID" \
  '{
    "@context": "https://aboutcircles.com/contexts/circles-namespace/",
    "@type": "NameIndexDoc",
    "head": $head,
    "entries": {
      ($entry_key): $entry_val
    }
  }')

INDEX_CID=$(pin_json "$INDEX_JSON" "NameIndexDoc")
echo "   Index CID: $INDEX_CID"

# ── Step E: Pin Profile ───────────────────────────────────────────────
echo ""
echo "── Step E: Pin Profile"

PROFILE_JSON=$(jq -n \
  --arg name "Test Seller" \
  --arg desc "Staging test seller for marketplace verification" \
  --arg ns_key "$ADDRESS_LOWER" \
  --arg ns_val "$INDEX_CID" \
  '{
    "@context": "https://aboutcircles.com/contexts/circles-profile/",
    "@type": "Profile",
    "name": $name,
    "description": $desc,
    "namespaces": {
      ($ns_key): $ns_val
    }
  }')

PROFILE_CID=$(pin_json "$PROFILE_JSON" "Profile")
echo "   Profile CID: $PROFILE_CID"

# ── Summary ───────────────────────────────────────────────────────────
echo ""
echo "============================================"
echo "  DOCUMENT CHAIN PINNED SUCCESSFULLY"
echo "============================================"
echo "  Product CID:  $PRODUCT_CID"
echo "  Chunk CID:    $CHUNK_CID"
echo "  Index CID:    $INDEX_CID"
echo "  Profile CID:  $PROFILE_CID"
echo "  Seller:       $ADDRESS_LOWER"
echo "  SKU:          $SKU"
echo "============================================"
echo ""
echo "Next steps:"
echo "  1. Register profile in pinning service DB:"
echo "     ssh indexer-staging2"
echo "     docker exec -i profile-pinning-db psql -U postgres -d profile_pinning <<SQL"
echo "     INSERT INTO roots (avatar, root_cid, block_number)"
echo "     VALUES ('$ADDRESS_LOWER', '$PROFILE_CID', 0)"
echo "     ON CONFLICT (avatar) DO UPDATE SET root_cid = EXCLUDED.root_cid, updated_at = now();"
echo ""
echo "     INSERT INTO profiles (address, cid, name)"
echo "     VALUES ('$ADDRESS_LOWER', '$PROFILE_CID', 'Test Seller')"
echo "     ON CONFLICT (address) DO UPDATE SET cid = EXCLUDED.cid, name = EXCLUDED.name, last_updated_at = now();"
echo "     SQL"
echo ""
echo "  2. Create market route (requires admin JWT):"
echo "     curl -X PUT ${STAGING_URL}/admin/routes \\"
echo "       -H 'Authorization: Bearer \$ADMIN_JWT' \\"
echo "       -H 'Content-Type: application/json' \\"
echo "       -d '{\"chainId\":$CHAIN_ID,\"seller\":\"$ADDRESS_LOWER\",\"sku\":\"$SKU\",\"isOneOff\":true,\"totalInventory\":100,\"enabled\":true}'"
echo ""
echo "  3. Verify catalog:"
echo "     curl -s '${STAGING_URL}/api/operator/${ADDRESS_LOWER}/catalog?avatars=${ADDRESS_LOWER}'"
