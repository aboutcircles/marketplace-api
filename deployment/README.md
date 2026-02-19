# Deployment
This directory contains two docker-compose files, one for development and one for production.

## Getting started
### 1. Copy and customize example .env file
#### 1.1. Copy the `.env.example` file to `.env`:
````shell
cp .env.example .env
````

#### 1.2. Edit `.env` and fill in:

##### 1.2.1 Database server configuration
The docker compose environmen contains one postgres instance that houses the databases of the three services (`circles_market_api`, `circles_codedisp`, `circles_odoo`).
* `POSTGRES_TAG`: The PostgreSQL image tag to use (e.g., `17-alpine`).
* `POSTGRES_USER`: The username for the postgresql root user.
* `POSTGRES_PASSWORD`: The password for the postgresql root user.

##### 1.2.2 Market API configuration
The market api is the only service in this setup that's reachable from outside via a proxy.
The following environment variables are used by the market api and the proxy:
* `MARKET_API_TAG`: The Market API image tag to use (e.g., `latest`).
* `DOMAIN_NAME`: The domain at which this market api instance will be accessible.
* `PUBLIC_BASE_URL`: The full external url at which this market api instance will be accessible.
* `ALLOWED_ORIGINS`: Comma-separated list of allowed origins for CORS.
* `MARKET_AUTH_ALLOWED_DOMAINS`: Comma-separated list of allowed domains for authentication.

The following variables are used solely by the market api. They configure profile storage (ipfs), blockchain and db access:
* `IPFS_GATEWAY_URL`: The IPFS gateway URL for accessing IPFS content.
* `IPFS_RPC_URL`: The IPFS RPC URL for interacting with the IPFS node (e.g. pinning).
* `IPFS_RPC_BEARER`: The bearer token for IPFS RPC authentication.
* `RPC`: The RPC endpoint for interacting with the blockchain.
* `MARKET_JWT_SECRET`: The JWT secret for signing and verifying tokens.
* `DB_MARKET_API_PASSWORD`: The password for the market-api db user.

Admin auth (Market admin app mints, adapters validate):
* `ADMIN_JWT_SECRET`: HMAC secret for admin JWT signing/validation.
* `ADMIN_JWT_ISSUER`: Issuer for admin JWTs (default `Circles.Market.Admin`).
* `ADMIN_JWT_AUDIENCE`: Audience for admin JWTs (default `market-admin`).
* `ADMIN_ADDRESSES`: Comma-separated allowlist of admin wallet addresses.
* `ADMIN_AUTH_ALLOWED_DOMAINS`: Allowed domains for admin SIWE base URL.
* `ADMIN_PUBLIC_BASE_URL`: Public base URL used in admin SIWE message.

Market admin proxy internals (for adapter admin calls):
* `ODOO_ADMIN_INTERNAL_URL`: Absolute URL for Odoo adapter admin host (e.g. `http://market-adapter-odoo:${MARKET_ODOO_ADMIN_PORT}`).
* `CODEDISP_ADMIN_INTERNAL_URL`: Absolute URL for CodeDispenser adapter admin host (e.g. `http://market-adapter-codedispenser:${MARKET_CODEDISP_ADMIN_PORT}`).
* `ADMIN_PROXY_ALLOWED_HOSTS`: Comma-separated host allowlist for admin proxy targets.

Outbound adapter auth (env-based shared secrets):
* `MARKET_ODOO_ADAPTER_TOKEN`: Shared secret for outbound calls to the Odoo adapter.
* `MARKET_CODE_DISPENSER_TOKEN`: Shared secret for outbound calls to the CodeDispenser adapter.
* `MARKET_OUTBOUND_HEADER_NAME`: (Optional) Header name to send, defaults to `X-Circles-Service-Key`.
* `MARKET_ODOO_ADAPTER_ORIGIN`: (Optional) Override the Odoo adapter origin.
* `MARKET_CODE_DISPENSER_ORIGIN`: (Optional) Override the CodeDispenser adapter origin.

##### 1.2.3 Code Dispenser configuration
* `MARKET_ADAPTER_CODEDISP_TAG`: The image tag for the code dispenser container (e.g., `latest`).
* `DB_CODEDISP_PASSWORD`: The password for the code dispenser db user.

##### 1.2.4 Odoo configuration
* `MARKET_ADAPTER_ODOO_TAG`: The image tag for the market adapter odoo container (e.g., `latest`).
* `DB_ODOO_PASSWORD`: The password for the odoo db user.


### 2. Dev environment
There are two variants of the same environment. One is meant for local development (`docker-compose.dev.yml`), the other
for production use (`docker-compose.prod.yml`)`.

For the rest of this guid we assume the development environment.

#### 2.1. Run the environment
Navigate to `deployment/` (from the repository root), then run `start-dev.sh`. This script will start all services in
`docker-compose.dev.yml` and output connection details for every service.

By default, `start-dev.sh` redacts secrets in its output. Pass `--show-secrets` if you explicitly want to print credentials.
```shell
cd deployment
./start-dev.sh
```

If everything succeeds, you should see an optput like the following.
If not, check if the .env file exists and its values are configured correctly.
```
[start-dev] Services started
[start-dev] ===========================================
[start-dev]  Development Environment Ready!
[start-dev] ===========================================

Market API
  Local URL:      http://localhost:65001
  Health Check:   http://localhost:65001/health

Adapters
  Odoo Adapter:       http://localhost:65002
  Code Dispenser:     http://localhost:65003

Admin Ports (loopback-only recommended)
  Market Admin API:   http://localhost:65005
  Odoo Admin API:     http://localhost:65006
  CodeDisp Admin API: http://localhost:65007

External Dependencies
  IPFS Gateway:       http://localhost:8000/ipfs/
  IPFS RPC:           http://localhost:5001/api/v0/
  Gnosis Chain RPC:   https://rpc.aboutcircles.com/

PostgreSQL Databases
  Host:     localhost
  Port:     65004
  User:     postgres
  Password: ***

  Market API Database
    Name:     circles_market_api
    User:     market_api
    Password: ***

  Code Dispenser Database
    Name:     circles_codedisp
    User:     codedisp
    Password: ***

  Odoo Database
    Name:     circles_odoo
    User:     odoo
    Password: ***

Connection Strings (psql)
  Market API:
    PGPASSWORD=*** psql -h localhost -p 65004 -U market_api -d circles_market_api

  Code Dispenser:
    PGPASSWORD=*** psql -h localhost -p 65004 -U codedisp -d circles_codedisp

  Odoo:
    PGPASSWORD=*** psql -h localhost -p 65004 -U odoo -d circles_odoo

[start-dev] Additional Commands:
  Stop environment:   ./stop-dev.sh
  View logs:          docker compose -f docker-compose.dev.yml logs -f
  Restart service:    docker compose -f docker-compose.dev.yml restart <service-name>

$
```
The output of the tool lists all endpoints of the service (provided and used).
The script is only a wrapper around the regular `docker compose` commands.
If you prefer, you can start the stack also like this:
```shell
docker compose -f docker-compose.dev.yml up -d
```

### 3. Production environment

#### 3.1. Admin access (loopback + SSH tunnel)
The production compose file exposes the Market admin port on loopback only. Nginx does **not** expose admin routes.
To reach admin endpoints from your workstation, create an SSH tunnel:

```bash
ssh -L 5090:127.0.0.1:${MARKET_ADMIN_PORT} your-host
```

Then call admin endpoints locally:

```bash
curl -i http://localhost:5090/admin/health
```



## Caveats
The database schema is partially created lazily:
* The `baskets` table only appears in the db once the first basket is created.
