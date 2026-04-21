#!/usr/bin/env sh
set -eu

# WP-CLI automation: installs WordPress + WooCommerce, creates REST API keys,
# and seeds a test product.  Runs inside the wordpress:cli container after
# WordPress is healthy.

WP="wp --path=/var/www/html"

echo "[wc-setup] Waiting for WordPress database..."
until $WP db check >/dev/null 2>&1; do
  sleep 2
done

# ── WordPress core install ───────────────────────────────────────────────────

echo "[wc-setup] Checking if WordPress is installed..."
if ! $WP core is-installed 2>/dev/null; then
  echo "[wc-setup] Installing WordPress core..."
  $WP core install \
    --url="${WC_SITE_URL:-http://wc-wordpress}" \
    --title="Circles Test Store" \
    --admin_user="${WC_ADMIN_USER:-admin}" \
    --admin_password="${WC_ADMIN_PASSWORD:-admin123}" \
    --admin_email="${WC_ADMIN_EMAIL:-admin@localhost.test}" \
    --skip-email
else
  echo "[wc-setup] WordPress already installed."
fi

echo "[wc-setup] Setting permalink structure (required for REST API)..."
$WP rewrite structure '/%postname%/' --hard 2>/dev/null || true

# ── mu-plugin: allow REST API Basic Auth over HTTP ────────────────────────────
# WooCommerce only allows Basic Auth over HTTPS by default.  For local Docker
# testing we install a mu-plugin that forces WP to report the connection as
# secure when the request targets the REST API.

MU_DIR="/var/www/html/wp-content/mu-plugins"
mkdir -p "$MU_DIR" 2>/dev/null || true
cat > "$MU_DIR/force-ssl-rest.php" <<'MUPLUGIN'
<?php
/**
 * Plugin Name: Force SSL for REST API (local dev)
 * Description: Makes WordPress report is_ssl()=true for REST API requests,
 *              allowing WooCommerce HTTP Basic Auth over plain HTTP.
 */
add_filter('pre_option_siteurl', function($value) {
    if (defined('REST_REQUEST') && REST_REQUEST) {
        return str_replace('http://', 'https://', $value);
    }
    return $value;
});

// Tell WP the connection is secure for REST requests
add_action('rest_api_init', function() {
    $_SERVER['HTTPS'] = 'on';
}, 1);
MUPLUGIN
echo "[wc-setup] mu-plugin installed: force-ssl-rest.php"

# ── WooCommerce install ──────────────────────────────────────────────────────

echo "[wc-setup] Installing WooCommerce..."
if ! $WP plugin is-installed woocommerce 2>/dev/null; then
  $WP plugin install woocommerce --activate
else
  $WP plugin activate woocommerce 2>/dev/null || true
fi

echo "[wc-setup] Running WooCommerce database setup..."
# WC sometimes needs page creation; ignore errors
$WP wc tool run install_pages --user=1 2>/dev/null || true

# ── REST API keys ────────────────────────────────────────────────────────────

# Generate deterministic-ish keys (safe for local testing)
CONSUMER_KEY="ck_test_$(cat /proc/sys/kernel/random/uuid | tr -d '-' | head -c 32)"
CONSUMER_SECRET="cs_test_$(cat /proc/sys/kernel/random/uuid | tr -d '-' | head -c 32)"

echo "[wc-setup] Creating WooCommerce REST API keys..."

# Write PHP to temp files — avoids shell quoting issues with ash/dash
KEYS_PHP="/tmp/wc-create-keys.php"
cat > "$KEYS_PHP" <<KEYSPHP
<?php
global \$wpdb;
\$wpdb->query("DELETE FROM {\$wpdb->prefix}woocommerce_api_keys WHERE description = 'Circles Integration Test'");
\$wpdb->insert(
    \$wpdb->prefix . 'woocommerce_api_keys',
    array(
        'user_id'         => 1,
        'description'     => 'Circles Integration Test',
        'permissions'     => 'read_write',
        'consumer_key'    => wc_api_hash( '${CONSUMER_KEY}' ),
        'consumer_secret' => '${CONSUMER_SECRET}',
        'truncated_key'   => substr( '${CONSUMER_KEY}', -7 ),
    ),
    array( '%d', '%s', '%s', '%s', '%s', '%s' )
);
if ( \$wpdb->last_error ) {
    fwrite( STDERR, 'DB error: ' . \$wpdb->last_error . PHP_EOL );
    exit(1);
}
echo 'API key created';
KEYSPHP
$WP eval-file "$KEYS_PHP"

# ── Seed test product ────────────────────────────────────────────────────────

echo "[wc-setup] Seeding test product..."

SEED_PHP="/tmp/wc-seed-product.php"
cat > "$SEED_PHP" <<'SEEDPHP'
<?php
$existing = wc_get_products(array('sku' => 'circles-test-tshirt', 'limit' => 1));
if ( ! empty( $existing ) ) {
    echo 'Product already exists (ID=' . $existing[0]->get_id() . ')';
} else {
    $product = new WC_Product_Simple();
    $product->set_name( 'Circles Test T-Shirt' );
    $product->set_sku( 'circles-test-tshirt' );
    $product->set_regular_price( '25.00' );
    $product->set_status( 'publish' );
    $product->set_manage_stock( true );
    $product->set_stock_quantity( 100 );
    $product->set_stock_status( 'instock' );
    $product->set_description( 'Test product for Circles marketplace integration' );
    $product->set_short_description( 'Test T-Shirt' );
    $product->save();
    echo 'Product created (ID=' . $product->get_id() . ')';
}
SEEDPHP
$WP eval-file "$SEED_PHP"

# ── Write credentials for test scripts ───────────────────────────────────────

cat > /var/www/html/wc-test-credentials.json <<CREDS
{
  "consumer_key": "${CONSUMER_KEY}",
  "consumer_secret": "${CONSUMER_SECRET}",
  "site_url": "${WC_SITE_URL:-http://wc-wordpress}",
  "test_product_sku": "circles-test-tshirt"
}
CREDS

echo ""
echo "[wc-setup] ── Setup complete ───────────────────────────────"
echo "[wc-setup] Consumer Key:    ${CONSUMER_KEY}"
echo "[wc-setup] Consumer Secret: ${CONSUMER_SECRET}"
echo "[wc-setup] Site URL:        ${WC_SITE_URL:-http://wc-wordpress}"
echo "[wc-setup] Test SKU:        circles-test-tshirt"
echo "[wc-setup] Credentials:     /var/www/html/wc-test-credentials.json"
