#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
COMPOSE_FILE="$SCRIPT_DIR/docker-compose.dev.yml"
ENV_FILE="$SCRIPT_DIR/.env"

# Color codes for output
BLUE='\033[0;34m'
GREEN='\033[0;32m'
NC='\033[0m' # No Color

log_info() {
    echo -e "${BLUE}[stop-dev]${NC} $1"
}

log_success() {
    echo -e "${GREEN}[stop-dev]${NC} $1"
}

stop_services() {
    log_info "Stopping development environment..."

    # Determine docker compose command (support both v1 and v2)
    if command -v docker compose &> /dev/null; then
        COMPOSE_CMD="docker compose --env-file $ENV_FILE"
    else
        COMPOSE_CMD="docker-compose --env-file $ENV_FILE"
    fi

    cd "$SCRIPT_DIR" && $COMPOSE_CMD -f docker-compose.dev.yml down --remove-orphans

    log_success "All services have been stopped"
}

# Main execution
main() {
    if [ ! -f "$ENV_FILE" ]; then
        echo "[stop-dev] Error: .env file not found at: $ENV_FILE" >&2
        exit 1
    fi

    if [ ! -f "$COMPOSE_FILE" ]; then
        echo "[stop-dev] Error: docker-compose.dev.yml not found at: $COMPOSE_FILE" >&2
        exit 1
    fi

    stop_services
}

main "$@"
