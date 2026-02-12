#!/usr/bin/env bash
# Convenience redirect — the real verify lives alongside the other ops scripts.
exec "$(dirname "$0")/verify-config.sh" "$@"
