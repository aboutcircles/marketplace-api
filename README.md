# Circles Market API

A .NET 10.0 implementation of the Circles Marketplace API, providing aggregated catalogs, cart management, and order fulfillment.

## Projects

- **Circles.Market.Api**: The main ASP.NET Core Web API service.
- **Circles.Market.Adapters.Odoo**: Adapter for Odoo integration.
- **Circles.Market.Adapters.CodeDispenser**: Adapter for digital code dispensation.
- **Circles.Market.Shared**: Shared models and logic across projects.

## Getting Started

### Prerequisites

- .NET 10.0 SDK
- Docker

### Development

The solution file is located at the repository root: `Circles.Market.sln`.

```bash
dotnet restore
dotnet build
```

### Docker Compose

You can run the stack using Docker Compose:

```bash
cd deployment
cp .env.example .env
# Edit .env to set required variables
docker compose up -d --build
```

This will:
- Build the `market-api`, `market-adapter-codedispenser`, and `market-adapter-odoo` images.
- Start a PostgreSQL 17 instance.
- Initialize the databases (provisioning only) using the `init-db` service.

Odoo adapter starts by default; it returns 404 until you configure `odoo_connections` via the admin API.

Each service creates its own schema at startup.
Cross-service admin configuration is done via the **Market admin API** (see docs/ops.md). Adapters only validate the Market-issued admin JWTs on their admin ports.

Default ports:
- Market API: `5084`
- Market Admin API: `5090` (loopback by default)
- CodeDispenser Adapter: `5680`
- CodeDispenser Admin API: `5690` (loopback by default)
- Odoo Adapter: `5678`
- Odoo Admin API: `5688` (loopback by default)
- IPFS API: `25001`
- IPFS Gateway: `28081` (on host, if not conflicting)
- PostgreSQL: `25433` (on host)

## Documentation

- [Getting Started & Troubleshooting](docs/quickstart.md)
- [Operations & DB Configuration Guide](docs/ops.md)
- [Architecture & Ownership](docs/architecture.md)
- [Configuration Guide](Circles.Market.Api/configuration.md)

## License

AGPLv3
