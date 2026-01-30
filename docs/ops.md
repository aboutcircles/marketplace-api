# Operations Guide

This document serves as the source of truth for managing the runtime configuration of Circles.Market services.

## What you are configuring

To bring a seller's products live on the marketplace, you must understand these core concepts:

*   **seller**: The Circles address (avatar) that owns the products.
*   **sku**: A unique string identifier for a product (e.g., `voucher-10-crc`).
*   **chainId**: The blockchain network ID (e.g., `100` for Gnosis Chain).
*   **inventoryFeed vs availabilityFeed**:
    *   `inventoryFeed`: Provides a quantitative stock level (e.g., "5 items left").
    *   `availabilityFeed`: Provides a simple "InStock" / "OutOfStock" status.
*   **fulfillmentEndpoint**: The URL called by the Market API after a payment is finalized to actually deliver the goods.
*   **"Internal URLs are fine"**: Since the Market API and Adapters run in the same Docker network, they should use service names for communication (e.g., `http://market-adapter-codedispenser:5680`).
*   **Inbound vs Outbound auth**:
    *   **Inbound (Adapter side)**: Adapters check the `X-Circles-Service-Key` header against their `trusted_callers` table.
    *   **Outbound (Market API side)**: Market API only attaches the `X-Circles-Service-Key` if a matching row exists in its `outbound_service_credentials` table. The match **must include the origin and port**.

## Flow: Bring a CodeDispenser voucher offer live

CodeDispenser is used for selling digital codes (vouchers, keys).

### 1. Choose identifiers
Decide on your `CHAIN_ID`, `SELLER` address, and a `SKU`. You also need a `POOL_ID` (a logical grouping of codes in CodeDispenser).

### 2. Seed a code pool
A "pool" is a bucket of codes. You must seed it before it can sell anything.
**Command:**
```bash
./scripts/ops.sh seed-pool <pool_id> <path_to_codes_file> --file
```
*   **What it does**: Loads codes into `code_pool_codes` (and ensures `code_pools` exists).
*   **What to check**: `SELECT count(*) FROM code_pool_codes WHERE pool_id = 'your_pool';`

### 3. Map (chainId, seller, sku) → pool
The adapter needs to know which pool to pull from when the Market API asks for a specific SKU.
**Command:**
```bash
./scripts/ops.sh mapping-codedisp <chain_id> <seller_address> <sku> <pool_id>
```
*   **What it does**: Writes a row to `code_mappings` in `circles_codedisp`.
*   **What to check**: `SELECT * FROM code_mappings WHERE sku = 'your_sku';`

### 4. Configure auth
The adapter will reject calls without a key, and Market API won't send a key unless told to.

#### 4.0 Generate a caller-id and shared secret (copy/paste)

You need two values:

* **caller_id**: a human-readable identifier for *who* is calling the adapter (usually “market-api in env X”).
  It’s used as the stable key in the adapter’s `trusted_callers` table. Pick something you can recognize later during debugging/rotation.
* **shared_secret**: the actual secret string. Market API sends it in `X-Circles-Service-Key`, and the adapter validates it by comparing a **SHA256 hash** stored in `trusted_callers`.

**Recommended constraints**

* `caller_id`: ASCII, no spaces, keep it stable (or include a timestamp if you want “rotation by new caller”).
  Good charset: `[a-z0-9-]`.
* `shared_secret`: generate as hex so it’s safe in shells, headers, and SQL (`openssl rand -hex 32`).

#### Option A: stable caller-id (recommended) + strong secret

```bash
ENVIRONMENT="${ENVIRONMENT:-local}"

CALLER_ID="market-api-${ENVIRONMENT}"
SHARED_SECRET="$(openssl rand -hex 32)"

echo "CALLER_ID=$CALLER_ID"
echo "SHARED_SECRET=$SHARED_SECRET"
```

Then run:

```bash
./scripts/ops.sh auth-codedisp "$CALLER_ID" "$SHARED_SECRET"
```

This pattern makes key rotation straightforward: keep the same `CALLER_ID` and just write a new secret.

#### Option B: unique caller-id per rotation (if you prefer audit-friendly history)

```bash
ENVIRONMENT="${ENVIRONMENT:-local}"

CALLER_ID="market-api-${ENVIRONMENT}-$(date -u +%Y%m%dT%H%M%SZ)"
SHARED_SECRET="$(openssl rand -hex 32)"

echo "CALLER_ID=$CALLER_ID"
echo "SHARED_SECRET=$SHARED_SECRET"
```

Run the same `auth-codedisp` command as above.

#### Don’t lose the secret

* The **adapter DB only stores the SHA256 hash**, so you can’t recover the secret from the DB later.
* Store `SHARED_SECRET` in your secret manager (1Password, Vault, Kubernetes secret, etc.). Never commit it.

#### 4.1 Verify auth rows exist and match what you generated

Compute the SHA256 hash you *expect* the adapter to have stored:

```bash
EXPECTED_HASH="$(printf %s "$SHARED_SECRET" | sha256sum | awk '{print $1}')"
echo "$EXPECTED_HASH"
```

Now check the adapter DB:

**CodeDispenser**

```bash
./scripts/ops.sh psql codedisp
```

```sql
SELECT caller_id, api_key_sha256
FROM trusted_callers
WHERE caller_id = 'market-api-local';
```

**Odoo**

```bash
./scripts/ops.sh psql odoo
```

```sql
SELECT caller_id, api_key_sha256
FROM trusted_callers
WHERE caller_id = 'market-api-local';
```

You want `api_key_sha256` (rendered as hex) to equal `$EXPECTED_HASH`.

Finally check Market API side (outbound creds):

```bash
./scripts/ops.sh psql market
```

```sql
SELECT endpoint_origin, path_prefix, service_key
FROM outbound_service_credentials
WHERE endpoint_origin IN (
  'http://market-adapter-codedispenser:5680',
  'http://market-adapter-odoo:5678'
);
```

You want to see the `service_key` present and the **origin includes the port**.

### 5. Set offer URLs
In the seller's catalog (on IPFS), the `inventoryFeed` and `fulfillmentEndpoint` must point to the CodeDispenser adapter.
*   **Inventory**: `http://market-adapter-codedispenser:5680/inventory/{chainId}/{seller}/{sku}`
*   **Fulfillment**: `http://market-adapter-codedispenser:5680/fulfill/{chainId}/{seller}`

### 6. Verify
Test the inventory path directly with a header:
```bash
curl -H "X-Circles-Service-Key: <your_secret>" http://localhost:5680/inventory/...
```
Then verify the Market API can proxy it (which proves outbound auth is working).

## Flow: Bring an Odoo-backed offer live

The Odoo adapter bridges the marketplace to an Odoo ERP instance.

### 1. Configure Odoo connection
The adapter needs to know how to talk to your Odoo instance.
**Command:**
```bash
./scripts/ops.sh odoo-connection <chain_id> <seller_address> <odoo_url> <odoo_db> <odoo_uid> <odoo_key> [partner_id]
```
*   **What it does**: Writes connection details (URL, DB, credentials) to `odoo_connections` in `circles_odoo`.
*   **What to check**: `SELECT * FROM odoo_connections WHERE seller_address = '...';`

### 2. Configure inventory mapping
Map your marketplace SKU to an internal Odoo Product Code.
**Command:**
```bash
./scripts/ops.sh odoo-mapping <chain_id> <seller_address> <sku> <odoo_product_code>
```
*   **What it does**: Writes to `inventory_mappings` in `circles_odoo`.
*   **What to check**: `SELECT * FROM inventory_mappings WHERE sku = '...';`

### 3. Configure auth
Same concept as CodeDispenser: both sides must know the shared secret. Use the variables from section **4.0** above.

**Command:**
```bash
./scripts/ops.sh auth-odoo "$CALLER_ID" "$SHARED_SECRET"
```
*   **What it does**: Updates `trusted_callers` (Odoo DB) and `outbound_service_credentials` (Market DB).
*   **What to check**: Use the verification steps in section **4.1** above. Default `odoo_origin` is `http://market-adapter-odoo:5678`.

### 4. Set offer URLs
Point the seller's offer URLs to the Odoo adapter:
*   **Inventory**: `http://market-adapter-odoo:5678/inventory/{chainId}/{seller}/{sku}`
*   **Fulfillment**: `http://market-adapter-odoo:5678/fulfill/{chainId}/{seller}`

### 5. Verify
Verify the paths via curl on the host ports (`5678`) or inside the network.

## Flow: Rotate a service key safely

Key rotation is two-sided. To avoid downtime:
1.  Add a **new** row to the adapter's `trusted_callers` with the new secret.
2.  Update the Market API's `outbound_service_credentials` to the new secret.
3.  Verify calls still work.
4.  Remove the old `trusted_callers` row.

**Warning**: `outbound_service_credentials` matching is sensitive to the **origin including port**. If you change the internal port of an adapter, you must update this table.

## Debugging (symptoms → cause → fix)

### 401 Unauthorized from adapter
*   **Cause**: `trusted_callers` missing, wrong secret, or wrong scope.
*   **Fix**: Re-run `auth-codedisp` or `auth-odoo`. Verify the `X-Circles-Service-Key` is being sent.

### "Blocked private address" / Market refusing outbound
*   **Cause**: Market API refuses to call internal IPs unless it has an explicit `outbound_service_credentials` match for that origin.
*   **Fix**: Check `outbound_service_credentials`. The `endpoint_origin` must exactly match the start of the URL (e.g. `http://market-adapter-codedispenser:5680`).

### 404 Not Found from Odoo adapter
*   **Cause**: Missing row in `inventory_mappings` or `odoo_connections`.
*   **Fix**: Re-run `odoo-mapping` or `odoo-connection`.

## Runtime configuration knobs (high impact)

### Payments poller
The Market API hosts a background payments poller.
*   `CHAIN_ID`: The blockchain network ID to monitor.
*   `POLL_SECONDS`: Interval between poll cycles (default: `5`).
*   `PAGE_SIZE`: Number of events to fetch per RPC call (default: `500`).
*   `CONFIRM_CONFIRMATIONS`: Blocks before a payment is considered "confirmed".
*   `FINALIZE_CONFIRMATIONS`: Blocks before a payment is considered "finalized".
*   `PAYMENT_GATEWAYS`: CSV list of payment gateway addresses to monitor.
*   **RPC Requirement**: The RPC endpoint must support `eth_blockNumber` and `circles_query` for `CrcV2_PaymentGateway.PaymentReceived`.

### Outbound request controls (SSRF / safety)
*   `OUTBOUND_AVAILABILITY_TIMEOUT_MS`: Timeout for checking item availability (default: `800`).
*   `OUTBOUND_INVENTORY_TIMEOUT_MS`: Timeout for inventory checks (default: `800`).
*   `OUTBOUND_FULFILLMENT_TIMEOUT_MS`: Timeout for fulfillment requests (default: `1500`).
*   `OUTBOUND_MAX_RESPONSE_BYTES`: Maximum size of adapter response (default: `65536`).
*   `OUTBOUND_MAX_REDIRECTS`: Maximum allowed HTTP redirects (default: `3`).

### SSE tuning
*   `SSE_CHANNEL_CAPACITY`: Max events queued per subscriber channel.
*   `SSE_MAX_SUBSCRIBERS_PER_KEY`: Max concurrent SSE connections per API key.

### Catalog cap
*   `CATALOG_MAX_AVATARS`: Maximum number of avatars to process in the catalog.

### Auth and Base URL
*   `MARKET_AUTH_ALLOWED_DOMAINS`: Allowed domains for authentication callbacks.
*   `PUBLIC_BASE_URL`: The externally accessible URL of the Market API.
*   `PUBLIC_BASE_SCHEME`: (Optional) Force `http` or `https` for generated links.

### Database
*   `DB_AUTO_MIGRATE`: If set to `true`, the Market API will attempt to run database migrations on startup. Currently no-op (schema is initialized via code on construction).

## Appendix: DB ownership and tables

Each service owns its own schema within the PostgreSQL instance:

| Service | Database Name | Purpose | Key Tables |
| :--- | :--- | :--- | :--- |
| **Market API** | `circles_market_api` | Core market data, offers, and credentials for adapters. | `outbound_service_credentials` |
| **CodeDispenser** | `circles_codedisp` | Management of digital code pools and mappings. | `trusted_callers`, `code_mappings`, `code_pools`, `code_pool_codes` |
| **Odoo Adapter** | `circles_odoo` | Connection details and inventory mapping for Odoo ERP. | `trusted_callers`, `odoo_connections`, `inventory_mappings` |

## Appendix: scripts/ops.sh command reference

| Command | Description |
| :--- | :--- |
| `auth-codedisp` | Wire Market API -> CodeDispenser auth |
| `mapping-codedisp` | Create CodeDispenser mapping |
| `seed-pool` | Seed CodeDispenser code pool |
| `auth-odoo` | Wire Market API -> Odoo auth |
| `odoo-connection` | Configure Odoo connection |
| `odoo-mapping` | Configure Odoo inventory mapping |
| `psql <db>` | Open PSQL shell (market|codedisp|odoo) |
| `show [db]` | Inspect configuration |
| `status` | High-level operator status view |
| `doctor` | Check system health and prerequisites |
