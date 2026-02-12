#!/bin/bash

# Shared helper library for Circles.Market scripts

# Print usage and exit (status 2 by default, or as provided)
print_usage() {
    local script_name=$1
    local usage_text=$2
    local exit_code=${3:-2}
    echo "Usage: $script_name $usage_text" >&2
    exit "$exit_code"
}

# Exit with error message
die() {
    echo "Error: $1" >&2
    exit 2
}

# Check if a command exists
need_cmd() {
    if ! command -v "$1" >/dev/null 2>&1; then
        die "Required command '$1' not found."
    fi
}

# Check if an environment variable is set
need_env() {
    local var_name=$1
    if [ -z "${!var_name}" ]; then
        die "Environment variable '$var_name' is required. Have you created your .env file?"
    fi
}

# Check if help was requested
help_requested() {
    for arg in "$@"; do
        if [[ "$arg" == "-h" || "$arg" == "--help" ]]; then
            return 0
        fi
    done
    return 1
}

# Load .env file if it exists
if [ -f .env ]; then
    set -a; source .env; set +a
elif [ -f ../.env ]; then
    set -a; source ../.env; set +a
fi

# Set compose project name so scripts work on both dev and staging.
# Ansible deploys with `-p circles-market-api`; mirror that here.
export COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-circles-market-api}"

# Validation helpers
is_int() {
    [[ "$1" =~ ^[0-9]+$ ]]
}

is_hex_address() {
    [[ "$1" =~ ^0x[0-9a-fA-F]{40}$ ]]
}
