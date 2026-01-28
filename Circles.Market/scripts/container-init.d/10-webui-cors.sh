#!/usr/bin/env sh
set -eu

# Enable CORS for local WebUI usage against the host-mapped API port.
# This is a dev convenience. Don't ship broad CORS to production.

ORIGINS_JSON='[
  "http://127.0.0.1:25001",
  "http://localhost:25001",
  "http://127.0.0.1:5001",
  "http://localhost:5001",
  "https://webui.ipfs.io"
]'

METHODS_JSON='["GET","POST","PUT","DELETE","OPTIONS"]'

HEADERS_JSON='[
  "Authorization",
  "Content-Type",
  "X-Requested-With",
  "Range",
  "User-Agent"
]'

EXPOSE_HEADERS_JSON='[
  "Location",
  "Ipfs-Hash",
  "Ipfs-Path",
  "X-Ipfs-Path",
  "X-Stream-Output"
]'

ipfs config --json API.HTTPHeaders.Access-Control-Allow-Origin "$ORIGINS_JSON"
ipfs config --json API.HTTPHeaders.Access-Control-Allow-Methods "$METHODS_JSON"
ipfs config --json API.HTTPHeaders.Access-Control-Allow-Headers "$HEADERS_JSON"
ipfs config --json API.HTTPHeaders.Access-Control-Expose-Headers "$EXPOSE_HEADERS_JSON"

echo "[ipfs-init] CORS configured for WebUI"
