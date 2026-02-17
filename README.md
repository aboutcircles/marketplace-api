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

The solution file is located at `Circles.Market/Circles.Market.sln`.

```bash
cd Circles.Market
dotnet restore
dotnet build
```

### Docker Compose

You can run the stack using Docker Compose:

```bash
cd Circles.Market
cp .env.example .env
# Edit .env to set required variables
docker compose up -d
```

This will:
- Build the `market-api`, `market-adapter-codedispenser`, and `market-adapter-odoo` images.
- Start a PostgreSQL 17 instance.
- Start an IPFS Kubo instance.
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

You can customize the configuration by creating a `.env` file in the `Circles.Market` directory.

## Documentation

- [Circles.Market.Api Documentation](Circles.Market.Api/README.md)
- [Configuration Guide](Circles.Market.Api/configuration.md)
- [Fulfillment workflow](fulfillment.md)

## License

AGPLv3
