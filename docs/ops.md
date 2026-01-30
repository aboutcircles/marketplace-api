# Operations Guide

This document serves as the source of truth for managing the runtime configuration of Circles.Market services.

## What you are configuring

To bring a seller's products live on the marketplace, you must understand these core concepts:

*   **seller**: The Circles address (avatar) that owns the products.
*   **sku**: A unique string identifier for a product (e.g., `voucher-10-crc`).
*   **chainId**: The blockchain network ID (e.g., `100` for Gnosis Chain).
*   **offer_type**: Selects which adapter handles inventory/availability/fulfillment for this SKU. Valid values:
    *   `odoo`: Use Odoo ERP adapter (supports inventory, availability, and fulfillment)
    *   `codedispenser`: Use CodeDispenser adapter (supports inventory and fulfillment only)
*   **is_one_off**: If `true`, this is a one-time sale (e.g., unique item) with no upstream adapter URL.
*   **inventoryFeed vs availabilityFeed**:
    *   `inventoryFeed`: Provides a quantitative stock level (e.g., "5 items left").
    *   `availabilityFeed`: Provides a simple "InStock" / "OutOfStock" status.
*   **fulfillmentEndpoint**: The URL called by the Market API after a payment is finalized to actually deliver the goods.

### Configuration via `market_service_routes`

Routes are configured in the `circles_market_api` database, table `market_service_routes`:

```sql
INSERT INTO market_service_routes (chain_id, seller_address, sku, offer_type, is_one_off, enabled)
VALUES (100, '0xseller...', 'my-sku', 'codedispenser', false, true);
```

The actual adapter URLs are derived from templates in `offer_types` table at runtime:

| offer_type | inventory template | availability template | fulfillment template |
|------------|-------------------|----------------------|---------------------|
| odoo | http://market-adapter-odoo:{MARKET_ODOO_ADAPTER_PORT}/inventory/{chain_id}/{seller}/{sku} | http://market-adapter-odoo:{MARKET_ODOO_ADAPTER_PORT}/availability/{chain_id}/{seller}/{sku} | http://market-adapter-odoo:{MARKET_ODOO_ADAPTER_PORT}/fulfill/{chain_id}/{seller} |
| codedispenser | http://market-adapter-codedispenser:{MARKET_CODE_DISPENSER_PORT}/inventory/{chain_id}/{seller}/{sku} | (none) | http://market-adapter-codedispenser:{MARKET_CODE_DISPENSER_PORT}/fulfill/{chain_id}/{seller} |

Template variables are expanded case-insensitively:
*   `{seller}` - lowercase seller address
*   `{sku}` - lowercase SKU
*   `{chain_id}` - numeric chain ID
*   Environment ports: `{MARKET_API_PORT}`, `{MARKET_ODOO_ADAPTER_PORT}`, `{MARKET_CODE_DISPENSER_PORT}`
*   **"Internal URLs are fine"**: Since the Market API and Adapters run in the same Docker network, they should use service names for communication (e.g., `http://market-adapter-codedispenser:5680`).
*   **Inbound vs Outbound auth**:
    *   **Inbound (Adapter side)**: Adapters compare the `X-Circles-Service-Key` header against the shared secret in `CIRCLES_SERVICE_KEY`.
    *   **Outbound (Market API side)**: Market API attaches the `X-Circles-Service-Key` header using `CIRCLES_SERVICE_KEY` (with optional per-adapter overrides).

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
Adapters reject calls without a key. All internal service-to-service calls require header `X-Circles-Service-Key` with a shared secret.

#### 4.0 Set a shared secret

Set the same value for Market API, CodeDispenser, and Odoo:

```bash
export CIRCLES_SERVICE_KEY="$(openssl rand -hex 32)"
```

Store the value in your secret manager (Vault, 1Password, Kubernetes secret, etc.). Do not commit it.

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
Use the shared `CIRCLES_SERVICE_KEY` from section **4.0** above. The Odoo adapter reads the same env var at startup.

### 4. Set offer URLs
Point the seller's offer URLs to the Odoo adapter:
*   **Inventory**: `http://market-adapter-odoo:5678/inventory/{chainId}/{seller}/{sku}`
*   **Fulfillment**: `http://market-adapter-odoo:5678/fulfill/{chainId}/{seller}`

### 5. Verify
Verify the paths via curl on the host ports (`5678`) or inside the network.

## Flow: Rotate a service key safely

Key rotation is a simple env change:
1. Update `CIRCLES_SERVICE_KEY` in Market API + adapters.
2. Deploy/restart the services.

## Debugging (symptoms → cause → fix)

### 401 Unauthorized from adapter
*   **Cause**: missing or wrong `CIRCLES_SERVICE_KEY`.
*   **Fix**: ensure `CIRCLES_SERVICE_KEY` is set and the `X-Circles-Service-Key` header is being sent.

### "Blocked private address" / Market refusing outbound
*   **Cause**: Market API refuses to call internal IPs unless it has an explicit env token configured for that origin.
*   **Fix**: Ensure the correct `MARKET_*_TOKEN` env var is set and the origin/port matches the adapter endpoint.

### 404 Not Found from Odoo adapter
*   **Cause**: Missing row in `inventory_mappings` or `odoo_connections`.
*   **Fix**: Re-run `odoo-mapping` or `odoo-connection`.

## Outbound adapter auth (env-based)

Market API uses env vars for outbound shared secrets:

* `CIRCLES_SERVICE_KEY` (shared secret for all adapters)
* Optional per-adapter overrides:
  * `MARKET_ODOO_ADAPTER_TOKEN`
  * `MARKET_CODE_DISPENSER_TOKEN`
* Optional `MARKET_OUTBOUND_HEADER_NAME` (default `X-Circles-Service-Key`)
* Optional origin overrides:
  * `MARKET_ODOO_ADAPTER_ORIGIN` (default `http://market-adapter-odoo:${MARKET_ODOO_ADAPTER_PORT}`)
  * `MARKET_CODE_DISPENSER_ORIGIN` (default `http://market-adapter-codedispenser:${MARKET_CODE_DISPENSER_PORT}`)

Warning: If a token env var is missing, Market API will attempt an unauthenticated request; private/local outbound guards may block it.

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
| **Market API** | `circles_market_api` | Core market data and offers. | `market_service_routes` |
| **CodeDispenser** | `circles_codedisp` | Management of digital code pools and mappings. | `code_mappings`, `code_pools`, `code_pool_codes` |
| **Odoo Adapter** | `circles_odoo` | Connection details and inventory mapping for Odoo ERP. | `odoo_connections`, `inventory_mappings` |

## Appendix: scripts/ops.sh command reference

| Command | Description |
| :--- | :--- |
| `mapping-codedisp` | Create CodeDispenser mapping |
| `seed-pool` | Seed CodeDispenser code pool |
| `odoo-connection` | Configure Odoo connection |
| `odoo-mapping` | Configure Odoo inventory mapping |
| `psql <db>` | Open PSQL shell (market|codedisp|odoo) |
| `show [db]` | Inspect configuration |
| `status` | High-level operator status view |
| `doctor` | Check system health and prerequisites |
