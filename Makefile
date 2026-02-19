# Circles.Market — developer Makefile
# Run from repo root. Compose files live in deployment/.

SHELL := /bin/bash
COMPOSE := docker compose --project-directory deployment
SLN := Circles.Market/Circles.Market.sln

# ─── Docker Compose ──────────────────────────────────────────────────────────

.PHONY: up down restart logs ps build-images

up: ## Start dev stack (build + detach)
	$(COMPOSE) up -d --build

down: ## Stop dev stack
	$(COMPOSE) down

restart: down up ## Restart dev stack

logs: ## Tail all container logs
	$(COMPOSE) logs -f --tail=100

ps: ## Show container status
	$(COMPOSE) ps

build-images: ## Build Docker images without starting
	$(COMPOSE) build

# ─── .NET Build & Test ───────────────────────────────────────────────────────

.PHONY: restore build test

restore: ## Restore NuGet packages
	dotnet restore $(SLN)

build: ## Build solution
	dotnet build $(SLN) -c Release --no-restore

test: ## Run all tests
	dotnet test $(SLN) -c Release --no-build --verbosity normal

# ─── Database ────────────────────────────────────────────────────────────────

.PHONY: psql-market psql-codedisp psql-odoo

psql-market: ## Open psql shell for market-api DB
	scripts/psql.sh market

psql-codedisp: ## Open psql shell for codedispenser DB
	scripts/psql.sh codedisp

psql-odoo: ## Open psql shell for odoo DB
	scripts/psql.sh odoo

# ─── Operations (wrappers around scripts/ops.sh) ────────────────────────────

.PHONY: status doctor show ops

status: ## Show operator status (containers + DB summary)
	scripts/ops.sh status

doctor: ## Check system health and prerequisites
	scripts/ops.sh doctor

show: ## Inspect all DB configuration
	scripts/ops.sh show

ops: ## Show ops.sh help
	scripts/ops.sh --help

# ─── Auth & Mapping Setup ───────────────────────────────────────────────────

.PHONY: auth-codedisp auth-odoo

auth-codedisp: ## Wire Market API -> CodeDispenser auth
	scripts/ops.sh auth-codedisp

auth-odoo: ## Wire Market API -> Odoo auth
	scripts/ops.sh auth-odoo

# ─── Parameterised targets ──────────────────────────────────────────────────
# Usage:
#   make seed-pool POOL_ID=my-pool CODE=test-code
#   make mapping-codedisp SELLER=0x... SKU=voucher-10 POOL_ID=my-pool
#   make odoo-connection SELLER=0x... URL=https://odoo.example.com DB=mydb UID=1 KEY=secret
#   make odoo-mapping SELLER=0x... SKU=voucher-10 PRODUCT_CODE=FOOD

.PHONY: seed-pool mapping-codedisp odoo-connection odoo-mapping

seed-pool: ## Seed a code pool (POOL_ID=, CODE=)
	scripts/ops.sh seed-pool $(POOL_ID) $(CODE)

mapping-codedisp: ## Create CodeDispenser mapping (SELLER=, SKU=, POOL_ID=)
	scripts/ops.sh mapping-codedisp $(SELLER) $(SKU) $(POOL_ID)

odoo-connection: ## Configure Odoo connection (SELLER=, URL=, DB=, UID=, KEY=)
	scripts/ops.sh odoo-connection $(SELLER) $(URL) $(DB) $(UID) $(KEY)

odoo-mapping: ## Configure Odoo inventory mapping (SELLER=, SKU=, PRODUCT_CODE=)
	scripts/ops.sh odoo-mapping $(SELLER) $(SKU) $(PRODUCT_CODE)

# ─── Verify & Clean ─────────────────────────────────────────────────────────

.PHONY: verify clean

verify: ## Run config verification checks
	scripts/verify-config.sh

clean: ## Remove build artifacts
	dotnet clean $(SLN) -c Release
	$(COMPOSE) down -v --remove-orphans

# ─── Help ────────────────────────────────────────────────────────────────────

.PHONY: help
help: ## Show this help
	@grep -E '^[a-zA-Z_-]+:.*?## .*$$' $(MAKEFILE_LIST) | sort | \
		awk 'BEGIN {FS = ":.*?## "}; {printf "\033[36m%-20s\033[0m %s\n", $$1, $$2}'

.DEFAULT_GOAL := help
