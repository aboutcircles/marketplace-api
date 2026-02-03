set -euo pipefail

MARKET_PORT=65001
ODOO_PORT=65002
CODEDISP_PORT=65003

MARKET_ADMIN_PORT=65005
ODOO_ADMIN_PORT=65006
CODEDISP_ADMIN_PORT=65007

CHECK_NGINX_PROXY=1
NGINX_BASE="http://localhost:18080"
NGINX_MARKET="${NGINX_BASE}/market"
NGINX_ADMIN="${NGINX_BASE}/market/admin"

ok()   { printf "\033[32m[OK]\033[0m %s\n" "$*"; }
fail() { printf "\033[31m[FAIL]\033[0m %s\n" "$*" >&2; exit 1; }

http_code() {
  curl -sS -o /dev/null -w "%{http_code}" "$@"
}

assert_code() {
  local expected="$1"; shift
  local url="$1"; shift
  local code
  code="$(http_code "$url" "$@")"
  if [ "$code" != "$expected" ]; then
    fail "$url expected HTTP $expected, got $code"
  fi
  ok "$url -> HTTP $code"
}

assert_json_ok_true() {
  local url="$1"
  local body
  body="$(curl -fsS "$url" || true)"
  echo "$body" | grep -q '"ok":true' || fail "$url expected JSON with ok:true, got: $body"
  ok "$url returned ok:true"
}

echo "=== Smoke test: public health ==="
assert_json_ok_true "http://localhost:${MARKET_PORT}/health"
assert_json_ok_true "http://localhost:${ODOO_PORT}/health"
assert_json_ok_true "http://localhost:${CODEDISP_PORT}/health"

echo
echo "=== Regression: admin separation ==="
assert_code 404 "http://localhost:${MARKET_PORT}/admin/health"
assert_code 404 "http://localhost:${ODOO_PORT}/admin/health"
assert_code 404 "http://localhost:${CODEDISP_PORT}/admin/health"

assert_code 404 "http://localhost:${MARKET_ADMIN_PORT}/health"
assert_code 404 "http://localhost:${ODOO_ADMIN_PORT}/health"
assert_code 404 "http://localhost:${CODEDISP_ADMIN_PORT}/health"

echo
echo "=== Admin auth basics (market admin) ==="
assert_code 401 "http://localhost:${MARKET_ADMIN_PORT}/admin/health"
assert_code 401 "http://localhost:${MARKET_ADMIN_PORT}/admin/routes"
assert_code 401 "http://localhost:${ODOO_ADMIN_PORT}/admin/health"
assert_code 401 "http://localhost:${CODEDISP_ADMIN_PORT}/admin/health"

echo
echo "=== Admin SIWE challenge/verify smoke ==="
challenge_json="$(
  curl -fsS -H "Content-Type: application/json" \
    -d '{"address":"0x0000000000000000000000000000000000000000","chainId":100}' \
    "http://localhost:${MARKET_ADMIN_PORT}/admin/auth/challenge"
)"
echo "$challenge_json" | grep -q '"challengeId"' || fail "challenge response missing challengeId: $challenge_json"
challenge_id="$(echo "$challenge_json" | sed -n 's/.*"challengeId":"\([^"]*\)".*/\1/p')"
[ -n "$challenge_id" ] || fail "could not parse challengeId from: $challenge_json"
ok "challenge created (challengeId=$challenge_id)"

# 65-byte well-formed-but-invalid signature: r=1, s=1, v=27 (0x1b)
DUMMY_SIG="0x$(printf '%064x%064x%02x' 1 1 27)"

assert_code 401 "http://localhost:${MARKET_ADMIN_PORT}/admin/auth/verify" \
  -H "Content-Type: application/json" \
  -d "{\"challengeId\":\"${challenge_id}\",\"signature\":\"${DUMMY_SIG}\"}"

echo
if [ "${CHECK_NGINX_PROXY}" -eq 1 ]; then
  echo "=== Nginx proxy (optional) ==="
  assert_json_ok_true "${NGINX_MARKET}/health"
  assert_code 401 "${NGINX_ADMIN}/health"
  assert_code 200 "${NGINX_ADMIN}/auth/challenge" \
    -H "Content-Type: application/json" \
    -d '{"address":"0x0000000000000000000000000000000000000000","chainId":100}'
  ok "nginx proxy admin challenge works"
else
  echo "=== Nginx proxy checks skipped ==="
fi

echo
ok "All smoke/regression checks passed."
