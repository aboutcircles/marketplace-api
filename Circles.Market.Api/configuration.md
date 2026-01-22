# Local install + config guide (Market API + CodeDispenser)

This guide assumes you’re running everything on one machine (Linux is easiest), and that the “DB-configured auth” changes (plus the Market API inventory proxy fix) are already in your code.

You’ll run:

* **Postgres** (stores carts/orders/payments + auth tables + credentials)
* **IPFS Kubo** (stores profiles/namespaces/payloads that Market API reads/writes)
* **Circles.Market.Api** (“Market API”)
* **Circles.Market.Adapters.CodeDispenser** (“CodeDispenser”)

## Mental model (what talks to what)

**Inbound auth (CodeDispenser)**
Market API (and any other trusted caller) calls CodeDispenser endpoints:

* `POST /fulfill/{chainId}/{seller}`
* `GET  /inventory/{chainId}/{seller}/{sku}`

CodeDispenser checks the request header:

* `X-Circles-Service-Key: <RAW_KEY>`

…and validates it against CodeDispenser’s Postgres table `trusted_callers`.

**Outbound auth (Market API)**
When Market API needs to call a seller’s upstream `fulfillmentEndpoint` / `inventoryFeed` / `availabilityFeed`, it decides whether to attach a header based on Market API’s Postgres table `outbound_service_credentials`.

That’s what makes “many APIs per seller / offer type” scale: it’s just rows in a table, not env vars or hardcoded allowlists.

---

# Getting started (quick path)

## 0) Prereqs

* **.NET SDK 10.x** (repo targets net10.0)
* **Docker**
* `psql` client (or any SQL tool)

Repo layout assumption:

* `Circles.Market.Api/`
* `Circles.Market.Adapters.CodeDispenser/`
* `Circles.Market/` (contains Docker Compose)

---

## 1) Start everything with Docker Compose

The easiest way to get a full environment (Postgres, IPFS, Market API, CodeDispenser) is using the provided `docker-compose.yml`.

```bash
cd Circles.Market
cp .env.example .env
# Edit .env to set MARKET_JWT_SECRET and other required vars
docker compose up -d
```

### One DB per process
The compose setup automatically provisions three distinct databases:
* `circles_market_api`
* `circles_codedisp`
* `circles_odoo`

Each service is responsible for its own schema creation/migration at startup.

---

## 2) Configure auth between services

Market API needs to be authorized to call CodeDispenser. We provide a helper script to set this up automatically:

```bash
# From the project root or Circles.Market directory
./Circles.Market/scripts/setup-market-codedisp-auth.sh
```

This script:
1. Generates (or accepts) a shared RAW_KEY.
2. Inserts the SHA256 of this key into the CodeDispenser DB (`circles_codedisp.trusted_callers`).
3. Inserts the plaintext key into the Market API DB (`circles_market_api.outbound_service_credentials`).

---

## 3) Seed Code Mappings and Pools

Since CodeDispenser mapping is now DB-driven, you need to insert mappings into the database.

### Create a mapping
```bash
./Circles.Market/scripts/set-codedisp-mapping.sh <seller_address> <sku> <pool_id>
```

### Seed a code pool
```bash
./Circles.Market/scripts/seed-code-pool.sh <pool_id> <test_code>
```

---

## 4) Interactive DB Access

Use the helper script to jump into any of the service databases:
```bash
./Circles.Market/scripts/psql.sh market
./Circles.Market/scripts/psql.sh codedisp
./Circles.Market/scripts/psql.sh odoo
```

---

# Manual Configuration (Alternative)

### Compute SHA256 (for CodeDispenser DB)

```bash
SHA_HEX="$(printf %s "$RAW_KEY" | sha256sum | awk '{print $1}')"
echo "$SHA_HEX"
```

## 5) Insert CodeDispenser inbound trust (CodeDispenser DB)

This authorizes Market API to call CodeDispenser:

```bash
psql "postgres://postgres:postgres@localhost:25433/circles_codedisp" <<SQL
INSERT INTO trusted_callers (
  caller_id,
  api_key_sha256,
  scopes,
  seller_address,
  chain_id,
  enabled
) VALUES (
  'market-api-local',
  decode('$SHA_HEX', 'hex'),
  ARRAY['fulfill','inventory'],
  NULL,
  NULL,
  true
);
SQL
```

You can restrict per seller/chain later (advanced section).

## 6) Insert Market API outbound credentials (Market DB)

Market API will attach the header **only** for endpoints that match these rows.

### Normalize origin correctly

Market API normalizes origin as `scheme://host:port` and fills default ports (443/80).
Here’s a helper to avoid mistakes:

```bash
python3 - <<'PY'
from urllib.parse import urlparse
u=urlparse("http://localhost:5680/fulfill/100/0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
scheme=u.scheme.lower()
host=u.hostname.lower()
port=u.port or (443 if scheme=="https" else 80)
print(f"{scheme}://{host}:{port}")
PY
```

For local CodeDispenser at `http://localhost:5680`, origin is:

* `http://localhost:5680`

Now insert two rows (fulfillment + inventory):

```bash
psql "postgres://postgres:postgres@localhost:25433/circles_market" <<SQL
INSERT INTO outbound_service_credentials (
  id, service_kind, endpoint_origin, path_prefix,
  seller_address, chain_id,
  header_name, api_key, enabled
) VALUES
(
  gen_random_uuid(),
  'fulfillment',
  'http://localhost:5680',
  '/fulfill',
  NULL,
  NULL,
  'X-Circles-Service-Key',
  '$RAW_KEY',
  true
),
(
  gen_random_uuid(),
  'inventory',
  'http://localhost:5680',
  '/inventory',
  NULL,
  NULL,
  'X-Circles-Service-Key',
  '$RAW_KEY',
  true
);
SQL
```

If your DB doesn’t have `gen_random_uuid()`, just paste a UUID yourself (or enable `pgcrypto` / `uuid-ossp`).

---

# Quick verification (before doing any “real” flow)

## 7) Verify CodeDispenser rejects missing auth

```bash
curl -i "http://localhost:5680/inventory/100/0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/tee-black"
```

Expect `401`.

## 8) Verify CodeDispenser accepts valid auth

```bash
curl -i \
  -H "X-Circles-Service-Key: $RAW_KEY" \
  "http://localhost:5680/inventory/100/0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/tee-black"
```

Expect `200` JSON like:

```json
{ "@type":"QuantitativeValue", "value": 3, "unitCode":"C62" }
```

## 9) Verify `/fulfill` returns `codes: []` or `codes: ["..."]` (never `code`)

```bash
curl -i \
  -H "X-Circles-Service-Key: $RAW_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "orderId":"ord_test",
    "paymentReference":"pay_test",
    "items":[{"sku":"tee-black","quantity":1}],
    "trigger":"finalized"
  }' \
  "http://localhost:5680/fulfill/100/0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
```

---

# Running both services every time (recommended local setup)

## Market API environment template

```bash
export POSTGRES_CONNECTION="Host=localhost;Port=5432;Database=circles_market;Username=postgres;Password=postgres"
export RPC="https://your-gnosis-rpc.example"
export IPFS_RPC_URL="http://127.0.0.1:5001/api/v0/"
export IPFS_GATEWAY_URL="http://127.0.0.1:8080/"
export IPFS_RPC_BEARER="local-dev"
export MARKET_JWT_SECRET="dev-secret-change-me"
export MARKET_AUTH_ALLOWED_DOMAINS="localhost"
export PUBLIC_BASE_URL="http://localhost:5084"
export PORT=5084
```

## CodeDispenser environment template

```bash
export POSTGRES_CONNECTION="Host=localhost;Port=5432;Database=circles_codedisp;Username=postgres;Password=postgres"
export CODE_POOLS_DIR="$PWD/pools"
export PORT=5680
```

---

# More detailed configuration (scaling beyond “one local service”)

## A) Restrict CodeDispenser callers by seller and/or chain

If you host CodeDispenser for many sellers but want different keys per seller:

```sql
UPDATE trusted_callers
SET seller_address = '0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
    chain_id = 100
WHERE caller_id = 'market-api-local';
```

Or add multiple rows with different `caller_id` and hashes.

**Tip:** keep `scopes` minimal:

* inventory-only callers: `ARRAY['inventory']`
* fulfillment-only callers: `ARRAY['fulfill']`

## B) Multiple upstream services per seller / offer type (Market API outbound)

This is where `path_prefix` becomes your routing tool.

Examples:

* one CodeDispenser for digital goods fulfillment:

    * `path_prefix = '/fulfill/digital'`
* another for event tickets:

    * `path_prefix = '/fulfill/tickets'`

Market API picks the **most specific** row by:

1. seller-specific > generic
2. chain-specific > generic
3. longest `path_prefix`
4. newest `created_at`

If there’s still a tie, it sends **no header** (safe failure).

## C) Key rotation without downtime

### Rotate for CodeDispenser inbound

1. Generate new raw key, insert a **new** trusted_callers row (or update existing row’s hash)
2. Deploy/update Market DB `outbound_service_credentials` to use the new raw key
3. After you’re sure traffic moved, revoke the old caller row

Revocation:

```sql
UPDATE trusted_callers
SET revoked_at = now(), enabled = false
WHERE caller_id = 'market-api-local';
```

### Rotate for Market API outbound

Insert a new outbound row with same match conditions but newer `created_at` (or just update the row).
Because selection uses newest as the last tie-breaker, newest wins if everything else matches.

## D) Don’t get burned by `endpoint_origin`

Market API compares `endpoint_origin` exactly, including the explicit port.
So for `https://example.com/...`, you likely want:

* `https://example.com:443`

For `http://example.com/...`, you likely want:

* `http://example.com:80`

If you’re unsure, compute it with the little python snippet above.

## E) Cache considerations

Market API caches outbound credential lookups for a few minutes.
When you change DB rows and want it to take effect immediately:

* restart Market API (simplest), or
* wait for the cache TTL to expire

---

# Troubleshooting checklist

## “401 from CodeDispenser even though I set the key”

* Verify you’re sending the header:

    * `X-Circles-Service-Key: <RAW_KEY>`
* Verify the DB row exists:

  ```sql
  SELECT caller_id, enabled, revoked_at, scopes
  FROM trusted_callers;
  ```
* Verify you hashed the key the same way:

    * Code uses SHA256 of the **UTF-8 string**. If you used `echo` without `-n`, you may have hashed a newline.

## “Market API isn’t sending the header”

* Ensure the upstream URL’s origin+path actually match your DB row:

  ```sql
  SELECT service_kind, endpoint_origin, path_prefix, seller_address, chain_id, enabled, revoked_at
  FROM outbound_service_credentials
  WHERE enabled = true AND revoked_at IS NULL;
  ```
* Make sure `endpoint_origin` has the correct port (443/80 defaults included).
* Make sure `service_kind` is exactly what the code uses (`fulfillment` or `inventory`).

## “Ambiguous credentials (Market API sends nothing)”

* You have two equally specific rows matching the same request.
* Fix by making one row more specific (seller/chain/path_prefix) or revoke/disable one.
