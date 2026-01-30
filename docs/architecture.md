# Architecture & Ownership

This document describes the high-level architecture of Circles.Market and defines the ownership rules for data and configuration.

## System Diagram

```mermaid
graph TD
    User([User / Frontend]) --> MarketAPI[Market API]

    subgraph "Core Services"
        MarketAPI
        IPFS[(IPFS)]
    end

    subgraph "Adapters"
        CodeDisp[CodeDispenser Adapter]
        OdooAdapter[Odoo Adapter]
    end

    subgraph "Storage"
        MarketDB[(Postgres: circles_market_api)]
        CodeDispDB[(Postgres: circles_codedisp)]
        OdooDB[(Postgres: circles_odoo)]
    end

    MarketAPI --> IPFS
    MarketAPI -- Auth + SKU --> CodeDisp
    MarketAPI -- Auth + SKU --> OdooAdapter

    MarketAPI <--> MarketDB
    CodeDisp <--> CodeDispDB
    OdooAdapter <--> OdooDB

    OdooAdapter --> OdooERP[External Odoo ERP]
```

## Ownership Rules

To keep the system maintainable and decouple services, we follow these ownership rules:

### 1. Database Provisioning
- **Provisioner (`init-db`):** Owns the creation of databases and users. It is a "one-shot" service that ensures the PostgreSQL instance is ready for the services.
- **Rule:** The provisioner never creates tables or seeds business data.

### 2. Schema Ownership
- **Services:** Each service (Market API, CodeDispenser, Odoo Adapter) owns its own database schema.
- **Rule:** Services are responsible for creating and migrating their own tables on startup. No service should ever touch another service's database.

### 3. Configuration Ownership
- **Scripts:** Runtime configuration (auth keys, seller connections, SKU mappings) is owned by the operator via scripts.
- **Rule:** Configuration rows are written by scripts in the `scripts/` directory. Services read this configuration at runtime but do not "own" the seeding of it.

## Where is X configured?

| If you want to change... | Go to... |
| :--- | :--- |
| JWT validation domains | `.env` (`MARKET_AUTH_ALLOWED_DOMAINS`) |
| Market -> Adapter auth | `CIRCLES_SERVICE_KEY` shared secret (env) |
| Seller Odoo credentials | `scripts/ops.sh odoo-connection` |
| SKU to Odoo product mapping | `scripts/ops.sh odoo-mapping` |
| SKU to Code pool mapping | `scripts/ops.sh mapping-codedisp` |
| Digital codes in a pool | `scripts/ops.sh seed-pool` |
