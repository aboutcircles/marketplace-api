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

## Admin JWTs (single issuer)

Admin JWTs are **only minted by the Market API admin app** via SIWE challenge/verify.
Adapters (Odoo/CodeDispenser) only **validate** these JWTs on their admin ports.

* Market admin auth endpoints: `http://<market-admin-host>:${MARKET_ADMIN_PORT}/admin/auth/challenge` and `/admin/auth/verify`
* Adapter admin ports are **not** internet-facing by default; expose only on loopback if needed.

Frontend integration guide (Svelte 5): see [`docs/admin-api-frontend-guide.md`](admin-api-frontend-guide.md).

### Recommended admin workflow (proxy)

1. **Get an admin JWT from the Market admin app** (`/admin/auth/challenge` + `/admin/auth/verify`).
2. **Use the Market admin proxy endpoints**:
   * `POST /admin/odoo-products`
   * `GET /admin/odoo-products`
   * `DELETE /admin/odoo-products/{chainId}/{seller}/{sku}`
   * `POST /admin/code-products`
   * `GET /admin/code-products`
   * `DELETE /admin/code-products/{chainId}/{seller}/{sku}`
   * `GET /admin/routes`
   * `PUT /admin/routes`
   * `DELETE /admin/routes/{chainId}/{seller}/{sku}`

These endpoints live on the Market admin port (`5090`) and proxy to adapter admin APIs while keeping
the public adapter ports free of `/admin/*` routes.

## Flow: Bring a CodeDispenser voucher offer live

CodeDispenser is used for selling digital codes (vouchers, keys).

### 1. Choose identifiers
Decide on your `CHAIN_ID`, `SELLER` address, and a `SKU`. You also need a `POOL_ID` (a logical grouping of codes in CodeDispenser).

### 2. Seed a code pool (admin API)
A "pool" is a bucket of codes. You must seed it before it can sell anything.
**Endpoint (recommended):**
```
POST /admin/code-products
```
Example (via Market admin port):
```bash
curl -H "Authorization: Bearer <ADMIN_JWT>" -H "Content-Type: application/json" \
  -d '{"chainId":100,"seller":"0xabc...","sku":"voucher-10","poolId":"pool-a","codes":["CODE1","CODE2"]}' \
  http://localhost:${MARKET_ADMIN_PORT}/admin/code-products
```
*   **What it does**: Proxies to CodeDispenser admin APIs and updates `market_service_routes` in Market DB.

### 3. Map (chainId, seller, sku) → pool (admin API)
The adapter needs to know which pool to pull from when the Market API asks for a specific SKU.
**Endpoint (recommended):**
```
POST /admin/code-products
```
Example (via Market admin port):
```bash
curl -H "Authorization: Bearer <ADMIN_JWT>" -H "Content-Type: application/json" \
  -d '{"chainId":100,"seller":"0xabc...","sku":"voucher-10","poolId":"pool-a"}' \
  http://localhost:${MARKET_ADMIN_PORT}/admin/code-products
```
*   **What it does**: Writes mapping via CodeDispenser admin API and updates Market route table.

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
*   **Inventory**: `http://market-adapter-codedispenser:${MARKET_CODE_DISPENSER_PORT}/inventory/{chainId}/{seller}/{sku}`
*   **Fulfillment**: `http://market-adapter-codedispenser:${MARKET_CODE_DISPENSER_PORT}/fulfill/{chainId}/{seller}`

### 6. Verify
Test the inventory path directly with a header:
```bash
curl -H "X-Circles-Service-Key: <your_secret>" http://localhost:${MARKET_CODE_DISPENSER_PORT}/inventory/...
```
Then verify the Market API can proxy it (which proves outbound auth is working).

## Flow: Bring an Odoo-backed offer live

The Odoo adapter bridges the marketplace to an Odoo ERP instance.

### 1. Configure Odoo connection (admin API)
The adapter needs to know how to talk to your Odoo instance.
**Endpoint (recommended):**
```
POST /admin/odoo-products
```
Example (via Market admin port):
```bash
curl -H "Authorization: Bearer <ADMIN_JWT>" -H "Content-Type: application/json" \
  -d '{"chainId":100,"seller":"0xabc...","sku":"my-sku","odooProductCode":"GC100","odooUrl":"https://your.odoo","odooDb":"mydb","odooUid":7,"odooKey":"secret"}' \
  http://localhost:${MARKET_ADMIN_PORT}/admin/odoo-products
```
*   **What it does**: Proxies to Odoo adapter admin APIs and updates Market route table.

### 2. Configure inventory mapping (admin API)
Map your marketplace SKU to an internal Odoo Product Code.
**Endpoint (recommended):**
```
POST /admin/odoo-products
```
Example (via Market admin port):
```bash
curl -H "Authorization: Bearer <ADMIN_JWT>" -H "Content-Type: application/json" \
  -d '{"chainId":100,"seller":"0xabc...","sku":"my-sku","odooProductCode":"GC100","odooUrl":"https://your.odoo","odooDb":"mydb","odooUid":7,"odooKey":"secret"}' \
  http://localhost:${MARKET_ADMIN_PORT}/admin/odoo-products
```
*   **What it does**: Proxies to Odoo adapter admin APIs and updates Market route table.

### 3. Configure auth
Use the shared `CIRCLES_SERVICE_KEY` from section **4.0** above. The Odoo adapter reads the same env var at startup.

### 4. Set offer URLs
Point the seller's offer URLs to the Odoo adapter:
*   **Inventory**: `http://market-adapter-odoo:${MARKET_ODOO_ADAPTER_PORT}/inventory/{chainId}/{seller}/{sku}`
*   **Fulfillment**: `http://market-adapter-odoo:${MARKET_ODOO_ADAPTER_PORT}/fulfill/{chainId}/{seller}`

### 5. Verify
Verify the public paths via curl on the public ports (`${MARKET_ODOO_ADAPTER_PORT}`).
Admin APIs are on `${MARKET_ODOO_ADMIN_PORT}` and require a Market-issued admin JWT.

## Flow: Rotate a service key safely

Key rotation is a simple env change:
1. Update `CIRCLES_SERVICE_KEY` in Market API + adapters.
2. Deploy/restart the services.

## Debugging (symptoms → cause → fix)

### 401 Unauthorized from admin endpoints
*   **Cause**: missing/invalid admin JWT or address not in `ADMIN_ADDRESSES`.
*   **Fix**: ensure you create a challenge, sign it with an allowlisted address, and pass `Authorization: Bearer <ADMIN_JWT>`.

### 401 Unauthorized from adapter
*   **Cause**: missing or wrong `CIRCLES_SERVICE_KEY`.
*   **Fix**: ensure `CIRCLES_SERVICE_KEY` is set and the `X-Circles-Service-Key` header is being sent.

### "Blocked private address" / Market refusing outbound
*   **Cause**: Market API refuses to call internal IPs unless it has an explicit env token configured for that origin.
*   **Fix**: Ensure the correct `MARKET_*_TOKEN` env var is set and the origin/port matches the adapter endpoint.

### 404 Not Found from Odoo adapter
*   **Cause**: Missing row in `inventory_mappings` or `odoo_connections`.
*   **Fix**: Recreate mappings via the Odoo admin API (`PUT /admin/connections` or `PUT /admin/mappings`).

## Advanced: direct adapter admin APIs

If you must call adapters directly, use the **admin ports** (not the public ports).
The public adapter ports (`5678` for Odoo, `5680` for CodeDispenser) do **not** expose `/admin/*` routes.

* **Odoo Adapter (admin port `${MARKET_ODOO_ADMIN_PORT}`)**
  * `PUT http://localhost:${MARKET_ODOO_ADMIN_PORT}/admin/connections`
  * `PUT http://localhost:${MARKET_ODOO_ADMIN_PORT}/admin/mappings`
* **CodeDispenser Adapter (admin port `${MARKET_CODEDISP_ADMIN_PORT}`)**
  * `POST http://localhost:${MARKET_CODEDISP_ADMIN_PORT}/admin/code-pools`
  * `POST http://localhost:${MARKET_CODEDISP_ADMIN_PORT}/admin/code-pools/{poolId}/seed`
  * `PUT http://localhost:${MARKET_CODEDISP_ADMIN_PORT}/admin/mappings`

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
*   `CONFIRM_CONFIRMATIONS`: Blocks before a payment is considered "confirmed" (default: `3`).
*   `FINALIZE_CONFIRMATIONS`: Blocks before a payment is considered "finalized" (default: `12`).
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

## Appendix: admin endpoints quick reference

| Service | Endpoint | Purpose |
| :--- | :--- | :--- |
| Market API (admin port) | `POST /admin/odoo-products` | Add/update Odoo-backed product (routes + Odoo config + mapping) |
| Market API (admin port) | `POST /admin/code-products` | Add/update CodeDispenser-backed product (routes + pools/mappings) |
| Market API | `GET /admin/routes` | Inspect configured routes |
| Odoo Adapter (admin port) | `PUT /admin/connections` | Upsert Odoo connection |
| Odoo Adapter (admin port) | `PUT /admin/mappings` | Upsert inventory mapping |
| CodeDispenser (admin port) | `POST /admin/code-pools` | Create code pool |
| CodeDispenser (admin port) | `POST /admin/code-pools/{poolId}/seed` | Seed codes |
| CodeDispenser (admin port) | `PUT /admin/mappings` | Upsert code mapping |
