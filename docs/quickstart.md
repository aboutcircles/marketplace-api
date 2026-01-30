# Quickstart Guide

This guide helps you get the Circles.Market environment up and running quickly.

## Prerequisites

- **Docker** (Desktop or Engine)
- **Docker Compose** (V2 recommended)
- **curl** (for health checks)

## Launching the Stack

1. **Enter the project directory:**
   ```bash
   cd Circles.Market
   ```

2. **Prepare your environment file:**
   ```bash
   cp .env.example .env
   ```
   *Note: For local development, the defaults in `.env.example` are sufficient.*

3. **Start the services:**
   ```bash
   docker compose up -d --build
   ```

## What you should see

After running `docker compose up`, you can monitor the logs with `docker compose logs -f`.

### Expected Log Output

- `init-db` service: Should run `provision-dbs.sh` and exit with code 0.
- `postgres` service: Should report "database system is ready to accept connections".
- `market-api` service: Should be listening on port `5084`.
- `market-adapter-codedispenser` service: Should be listening on port `5680`.
- `market-adapter-odoo` service: Should be listening on port `5678`.

### Health Check

Verify the Market API is responding:
```bash
curl http://localhost:5084/health
```

Inside the docker network, the adapters listen on their default ports (5680 for CodeDispenser, 5678 for Odoo). On localhost, they are mapped to `5680` and `5678` via `docker-compose.yml`.
Expected response: `Healthy` (or a JSON object with status).

## Empty Config is OK

On a fresh start:
- **Odoo Adapter** and **CodeDispenser Adapter** will run successfully but will have empty databases.
- Endpoints requiring configuration (e.g., trying to buy an item that isn't mapped) will return deterministic `404 Not Found` or `401 Unauthorized` errors rather than crashing the service.
- Use `scripts/ops.sh` to configure these adapters as needed. See [Operations Guide](ops.md) for details.
