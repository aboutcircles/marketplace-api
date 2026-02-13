# Local install + config guide (Market API + CodeDispenser)

This guide assumes you’re running everything on one machine (Linux is easiest), and that env-based shared-secret auth is enabled.

You’ll run:

* **Postgres** (stores carts/orders/payments + adapter mappings)
* **IPFS Kubo** (stores profiles/namespaces/payloads that Market API reads/writes)
* **Circles.Market.Api** (“Market API”)
* **Circles.Market.Adapters.CodeDispenser** (“CodeDispenser”)

## Mental model (what talks to what)

**Inbound auth (CodeDispenser)**
Market API (and any other trusted caller) calls CodeDispenser endpoints:

* `POST /fulfill/{chainId}/{seller}`
* `GET  /inventory/{chainId}/{seller}/{sku}`

CodeDispenser checks the request header:

* `X-Circles-Service-Key: <CIRCLES_SERVICE_KEY>`

…and validates it against the shared secret configured via `CIRCLES_SERVICE_KEY`.

**Outbound auth (Market API)**
When Market API needs to call a seller’s upstream `fulfillmentEndpoint` / `inventoryFeed` / `availabilityFeed`, it attaches the `X-Circles-Service-Key` header using `CIRCLES_SERVICE_KEY` (with optional per-adapter overrides).

---

# Getting started (quick path)

## 0) Prereqs

* **.NET SDK 10.x** (repo targets net10.0)
* **Docker**
* `psql` client (or any SQL tool)

Repo layout assumption:

* `Circles.Market.Api/`
* `Circles.Market.Adapters.CodeDispenser/`
* `/` (contains Docker Compose)

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

Market API needs to be authorized to call CodeDispenser. Set the shared secret once and reuse it across services:

```bash
export CIRCLES_SERVICE_KEY="$(openssl rand -hex 32)"
```

---

## 3) Seed Code Mappings and Pools

Since CodeDispenser mapping is now DB-driven, you need to insert mappings via the admin API.

### Create a mapping (admin API)
```bash
curl -H "Authorization: Bearer <ADMIN_JWT>" -H "Content-Type: application/json" \
  -d '{"chainId":100,"seller":"0xabc...","sku":"tee-black","poolId":"pool-a"}' \
  http://localhost:5090/admin/code-products
```

### Seed a code pool (admin API)
```bash
curl -H "Authorization: Bearer <ADMIN_JWT>" -H "Content-Type: application/json" \
  -d '{"chainId":100,"seller":"0xabc...","sku":"tee-black","poolId":"pool-a","codes":["CODE1","CODE2"]}' \
  http://localhost:5090/admin/code-products
```

### Obtain an admin JWT (SIWE)
Admin endpoints require a short-lived JWT obtained via the admin auth flow.

1) Request a challenge:
```bash
curl -H "Content-Type: application/json" \
  -d '{"address":"0xYourAdminAddress","chainId":100}' \
  http://localhost:5090/admin/auth/challenge
```

2) Sign the returned `message` with your wallet, then verify:
```bash
curl -H "Content-Type: application/json" \
  -d '{"challengeId":"<uuid>","signature":"0x..."}' \
  http://localhost:5090/admin/auth/verify
```

3) Use the `token` as `Authorization: Bearer <token>`.

---

## 4) Configure Odoo connections + mappings

If you are using the Odoo adapter, first register the connection for `(chainId, seller)`, then map SKUs.

### Create/update an Odoo connection (admin API)
```bash
curl -H "Authorization: Bearer <ADMIN_JWT>" -H "Content-Type: application/json" \
  -d '{"chainId":100,"seller":"0xabc...","odooUrl":"https://your.odoo","odooDb":"mydb","odooUid":7,"odooKey":"secret"}' \
  http://localhost:5090/admin/odoo-connections
```

### Create an Odoo product mapping (admin API)
```bash
curl -H "Authorization: Bearer <ADMIN_JWT>" -H "Content-Type: application/json" \
  -d '{"chainId":100,"seller":"0xabc...","sku":"my-sku","odooProductCode":"GC100"}' \
  http://localhost:5090/admin/odoo-products
```

---

## 5) Interactive DB Access

Use `psql` or your favorite DB tool to connect to the service databases directly.

---

## Manual Configuration (Alternative)

No DB configuration is required for auth now; all services read `CIRCLES_SERVICE_KEY` from the environment.

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
  -H "X-Circles-Service-Key: $CIRCLES_SERVICE_KEY" \
  "http://localhost:5680/inventory/100/0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/tee-black"
```

Expect `200` JSON like:

```json
{ "@type":"QuantitativeValue", "value": 3, "unitCode":"C62" }
```

## 9) Verify `/fulfill` returns `codes: []` or `codes: ["..."]` (never `code`)

```bash
curl -i \
  -H "X-Circles-Service-Key: $CIRCLES_SERVICE_KEY" \
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
export ODOO_ADMIN_INTERNAL_URL="http://localhost:5688"
export CODEDISP_ADMIN_INTERNAL_URL="http://localhost:5690"
export ADMIN_PROXY_ALLOWED_HOSTS="localhost,127.0.0.1"
export RPC="https://your-gnosis-rpc.example"
export IPFS_RPC_URL="http://127.0.0.1:5001/api/v0/"
export IPFS_GATEWAY_URL="http://127.0.0.1:8080/"
export IPFS_RPC_BEARER="local-dev"
export MARKET_JWT_SECRET="dev-secret-change-me"
export ADMIN_JWT_SECRET="dev-admin-secret"
export ADMIN_ADDRESSES="0xYourAdminAddress"
export ADMIN_AUTH_ALLOWED_DOMAINS="localhost"
export ADMIN_PUBLIC_BASE_URL="http://localhost:5090"
export MARKET_AUTH_ALLOWED_DOMAINS="localhost"
export PUBLIC_BASE_URL="http://localhost:5084"
export PORT=5084
export MARKET_ADMIN_PORT=5090
```

## CodeDispenser environment template

```bash
export POSTGRES_CONNECTION="Host=localhost;Port=5432;Database=circles_codedisp;Username=postgres;Password=postgres"
export CODE_POOLS_DIR="$PWD/pools"
export PORT=5680
export CODEDISP_ADMIN_PORT=5690
```

## Odoo adapter environment template

```bash
export POSTGRES_CONNECTION="Host=localhost;Port=5432;Database=circles_odoo;Username=postgres;Password=postgres"
export PORT=5678
export ODOO_ADMIN_PORT=5688
```

---

## Auth-service JWKS (opt-in dual-scheme)

The Market API supports an optional second authentication scheme using RS256/JWKS from an external auth service. When enabled, both local SIWE (HS256) and auth-service (RS256) JWTs are accepted.

```bash
# Enable by setting AUTH_SERVICE_URL (empty or unset = disabled)
export AUTH_SERVICE_URL="https://circles-auth-service-prod-oa4ea.ondigitalocean.app/auth"
export AUTH_JWT_ISSUER="circles-auth"     # default: circles-auth
export AUTH_JWT_AUDIENCE="market-api"      # default: market-api
```

The JWKS public keys are fetched from `{AUTH_SERVICE_URL}/.well-known/jwks.json` and cached for 10 minutes.

---

# More detailed configuration (scaling beyond "one local service")

Key rotation and overrides are handled via env vars; use per-adapter overrides only if you need different secrets per service.

---

# Troubleshooting checklist

## “401 from CodeDispenser even though I set the key”

* Verify you’re sending the header:

    * `X-Circles-Service-Key: <CIRCLES_SERVICE_KEY>`
* Verify the env var is set for the adapter process.

## “Market API isn’t sending the header”

* Ensure `CIRCLES_SERVICE_KEY` is set for the Market API process.
* If you use per-adapter overrides, ensure `MARKET_*_TOKEN` is set.
