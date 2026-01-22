# Circles.Market

A marketplace API built with .NET that enables trading of Circles tokens and integrates with various systems including IPFS, Code Dispenser, and Odoo.

## Quickstart

1.  `cd Circles.Market`
2.  `cp .env.example .env`
3.  `docker compose up -d --build`
4.  Verify health: `curl http://localhost:5084/health`
5.  (Optional) Configure auth and mappings: `./scripts/ops.sh`

## Documentation

*   [Getting Started & Troubleshooting](docs/quickstart.md)
*   [Operations & DB Configuration Guide](docs/ops.md)
*   [Architecture & Ownership](docs/architecture.md)
