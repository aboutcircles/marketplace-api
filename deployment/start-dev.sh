#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
COMPOSE_FILE="$SCRIPT_DIR/docker-compose.dev.yml"
ENV_FILE="$SCRIPT_DIR/.env"

# Color codes for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
CYAN='\033[0;36m'
BOLD='\033[1m'
NC='\033[0m' # No Color

log_info() {
    echo -e "${BLUE}[start-dev]${NC} $1"
}

log_success() {
    echo -e "${GREEN}[start-dev]${NC} $1"
}

log_warn() {
    echo -e "${YELLOW}[start-dev]${NC} $1"
}

log_error() {
    echo -e "${RED}[start-dev]${NC} $1" >&2
}

check_prerequisites() {
    log_info "Checking prerequisites..."

    if ! command -v docker &> /dev/null; then
        log_error "Docker is not installed or not in PATH"
        exit 1
    fi

    if ! command -v docker compose &> /dev/null && ! command -v docker-compose &> /dev/null; then
        log_error "Docker Compose is not available. Please ensure Docker Compose V2 is installed."
        exit 1
    fi

    if [ ! -f "$ENV_FILE" ]; then
        log_error ".env file not found at: $ENV_FILE"
        echo ""
        log_warn "Please create the .env file by copying and customizing the example:"
        log_warn "  cp .env.example .env"
        log_warn ""
        log_warn "Then edit .env and fill in all required configuration values."
        exit 1
    fi

    if [ ! -f "$COMPOSE_FILE" ]; then
        log_error "docker-compose.dev.yml not found at: $COMPOSE_FILE"
        exit 1
    fi

    # Load environment variables from .env file
    log_info "Loading configuration from .env..."
    set -a
    source "$ENV_FILE"
    set +a

    # Apply defaults for optional ports if not set in .env
    : "${MARKET_API_PORT:=5084}"
    : "${MARKET_ODOO_ADAPTER_PORT:=5678}"
    : "${MARKET_CODE_DISPENSER_PORT:=5680}"

    # Check for critical environment variables (now that they're loaded)
    local missing_vars=()

    # Database configuration (required)
    if [ -z "$POSTGRES_USER" ] || [ -z "$POSTGRES_PASSWORD" ]; then
        missing_vars+=("POSTGRES_USER, POSTGRES_PASSWORD")
    fi

    # Database names and users (at least MARKET_API is required for the app to function)
    if [ -z "$DB_MARKET_API" ] || [ -z "$DB_MARKET_API_USER" ]; then
        missing_vars+=("DB_MARKET_API, DB_MARKET_API_USER")
    fi

    if [ ${#missing_vars[@]} -gt 0 ]; then
        log_error "Missing required configuration:"
        for var in "${missing_vars[@]}"; do
            log_warn "  - $var"
        done
        echo ""
        log_warn "Please check your .env file and ensure all variables are set."
        exit 1
    fi

    log_success "All prerequisites met"
}

start_services() {
    log_info "Starting development environment..."

    # Determine docker compose command (support both v1 and v2)
    if command -v docker compose &> /dev/null; then
        COMPOSE_CMD="docker compose --env-file $ENV_FILE"
    else
        COMPOSE_CMD="docker-compose --env-file $ENV_FILE"
    fi

    # Start services in detached mode. Docker Compose handles waiting for dependencies
    # via depends_on with health conditions defined in the compose file.
    cd "$SCRIPT_DIR" && $COMPOSE_CMD -f docker-compose.dev.yml up -d --build

    log_success "Services started."
}

output_endpoints() {
    # Re-load environment variables (they're already loaded but good practice)
    set -a
    source "$ENV_FILE" 2>/dev/null || true
    set +a

    # Apply defaults for ports if not set
    local market_api_port="${MARKET_API_PORT:-5084}"
    local odoo_adapter_port="${MARKET_ODOO_ADAPTER_PORT:-5678}"
    local code_dispenser_port="${MARKET_CODE_DISPENSER_PORT:-5680}"

    echo ""
    log_success "==========================================="
    log_success " Development Environment Ready!"
    log_success "==========================================="
    echo ""

    # Display Market API endpoints
    echo -e "${BOLD}${CYAN}Market API${NC}"
    echo -e "  Local URL:      ${BLUE}http://localhost:${market_api_port}${NC}"
    echo -e "  Health Check:   ${BLUE}http://localhost:${market_api_port}/health${NC}"
    echo ""

    # Display Adapter endpoints
    echo -e "${BOLD}${CYAN}Adapters${NC}"
    echo -e "  Odoo Adapter:       ${BLUE}http://localhost:${odoo_adapter_port}${NC}"
    echo -e "  Code Dispenser:     ${BLUE}http://localhost:${code_dispenser_port}${NC}"
    echo ""

    # Check if nginx container exists and is running
    local nginx_running=$(docker ps --format '{{.Names}}' | grep -E 'nginx$' || true)

    if [ -n "$nginx_running" ]; then
        echo -e "${BOLD}${CYAN}Proxy${NC}"
        echo -e "  Nginx Proxy:        ${BLUE}http://localhost:18080${NC}"
        echo ""
    fi

    # Display external dependencies
    echo -e "${BOLD}${YELLOW}External Dependencies${NC}"

    if [ -n "${IPFS_GATEWAY_URL:-}" ]; then
        echo -e "  IPFS Gateway:       ${BLUE}${IPFS_GATEWAY_URL}${NC}"
    fi

    if [ -n "${IPFS_RPC_URL:-}" ]; then
        echo -e "  IPFS RPC:           ${BLUE}${IPFS_RPC_URL}${NC}"
    fi

    if [ -n "${RPC:-}" ]; then
        echo -e "  Gnosis Chain RPC:   ${BLUE}${RPC}${NC}"
    fi
    echo ""

    # Display PostgreSQL connection details for all databases
    echo -e "${BOLD}${GREEN}PostgreSQL Databases${NC}"
    echo -e "  Host:     ${BLUE}localhost${NC}"
    echo -e "  Port:     ${BLUE}${MARKET_POSTGRES_PORT}${NC}"
    echo -e "  User:     ${BLUE}${POSTGRES_USER}${NC}"
    echo -e "  Password: ${GREEN}${POSTGRES_PASSWORD}${NC}"
    echo ""

    # Individual database connection strings
    if [ -n "${DB_MARKET_API:-}" ] && [ -n "${DB_MARKET_API_USER:-}" ]; then
        local market_pass="${DB_MARKET_API_PASSWORD:-not_set}"
        echo -e "  ${BOLD}Market API Database${NC}"
        echo -e "    Name:     ${BLUE}${DB_MARKET_API}${NC}"
        echo -e "    User:     ${BLUE}${DB_MARKET_API_USER}${NC}"
        echo -e "    Password: ${GREEN}${market_pass}${NC}"
        echo ""
    fi

    if [ -n "${DB_CODEDISP:-}" ] && [ -n "${DB_CODEDISP_USER:-}" ]; then
        local codedisp_pass="${DB_CODEDISP_PASSWORD:-not_set}"
        echo -e "  ${BOLD}Code Dispenser Database${NC}"
        echo -e "    Name:     ${BLUE}${DB_CODEDISP}${NC}"
        echo -e "    User:     ${BLUE}${DB_CODEDISP_USER}${NC}"
        echo -e "    Password: ${GREEN}${codedisp_pass}${NC}"
        echo ""
    fi

    if [ -n "${DB_ODOO:-}" ] && [ -n "${DB_ODOO_USER:-}" ]; then
        local odoo_pass="${DB_ODOO_PASSWORD:-not_set}"
        echo -e "  ${BOLD}Odoo Database${NC}"
        echo -e "    Name:     ${BLUE}${DB_ODOO}${NC}"
        echo -e "    User:     ${BLUE}${DB_ODOO_USER}${NC}"
        echo -e "    Password: ${GREEN}${odoo_pass}${NC}"
        echo ""
    fi

    # Display connection strings for convenience
    echo -e "${BOLD}${CYAN}Connection Strings (psql)${NC}"

    if [ -n "${DB_MARKET_API:-}" ] && [ -n "${DB_MARKET_API_USER:-}" ]; then
        local market_pass="${DB_MARKET_API_PASSWORD:-${POSTGRES_PASSWORD}}"
        echo -e "  ${BOLD}Market API:${NC}"
        echo -e "    PGPASSWORD=${market_pass} psql -h localhost -p ${MARKET_POSTGRES_PORT} -U ${DB_MARKET_API_USER} -d ${DB_MARKET_API}"
        echo ""
    fi

    if [ -n "${DB_CODEDISP:-}" ] && [ -n "${DB_CODEDISP_USER:-}" ]; then
        local codedisp_pass="${DB_CODEDISP_PASSWORD:-${POSTGRES_PASSWORD}}"
        echo -e "  ${BOLD}Code Dispenser:${NC}"
        echo -e "    PGPASSWORD=${codedisp_pass} psql -h localhost -p ${MARKET_POSTGRES_PORT} -U ${DB_CODEDISP_USER} -d ${DB_CODEDISP}"
        echo ""
    fi

    if [ -n "${DB_ODOO:-}" ] && [ -n "${DB_ODOO_USER:-}" ]; then
        local odoo_pass="${DB_ODOO_PASSWORD:-${POSTGRES_PASSWORD}}"
        echo -e "  ${BOLD}Odoo:${NC}"
        echo -e "    PGPASSWORD=${odoo_pass} psql -h localhost -p ${MARKET_POSTGRES_PORT} -U ${DB_ODOO_USER} -d ${DB_ODOO}"
        echo ""
    fi

    # Display additional commands
    log_info "Additional Commands:"
    echo "  Stop environment:   ./stop-dev.sh"
    echo "  View logs:          docker compose -f docker-compose.dev.yml logs -f"
    echo "  Restart service:    docker compose -f docker-compose.dev.yml restart <service-name>"
    echo ""
}

# Main execution
main() {
    log_info "Development Environment Starter"
    echo ""

    check_prerequisites
    start_services
    output_endpoints
}

main "$@"
