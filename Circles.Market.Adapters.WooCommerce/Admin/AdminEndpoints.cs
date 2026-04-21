using Circles.Market.Adapters.WooCommerce.Admin;
using Microsoft.AspNetCore.Authorization;
using Npgsql;

namespace Circles.Market.Adapters.WooCommerce.Admin;

public static class AdminEndpoints
{
    public static void MapWooCommerceAdminApi(this WebApplication app, string adminBasePath, string postgresConn)
    {
        var group = app.MapGroup(adminBasePath).RequireAuthorization();

        group.MapGet("/health", () => Results.Json(new { ok = true }));

        // ── Connections ────────────────────────────────────────────────────────

        group.MapGet("/connections", async (CancellationToken ct) =>
        {
            var result = new List<WooCommerceConnectionDto>();
            await using var conn = new NpgsqlConnection(postgresConn);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT chain_id, seller_address, wc_base_url, wc_consumer_key, default_customer_id,
                       order_status, timeout_ms, fulfill_inherit_request_abort, enabled, revoked_at
                FROM wc_connections
                ORDER BY chain_id, seller_address
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                result.Add(new WooCommerceConnectionDto
                {
                    ChainId = reader.GetInt64(0),
                    Seller = reader.GetString(1),
                    WcBaseUrl = reader.GetString(2),
                    WcConsumerKey = Mask(reader.GetString(3)),
                    DefaultCustomerId = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    OrderStatus = reader.GetString(5),
                    TimeoutMs = reader.GetInt32(6),
                    FulfillInheritRequestAbort = reader.GetBoolean(7),
                    Enabled = reader.GetBoolean(8),
                    RevokedAt = reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset?>(9)
                });
            }
            return Results.Json(result);
        });

        group.MapPut("/connections", async (WooCommerceConnectionUpsertRequest req, CancellationToken ct) =>
        {
            if (req.ChainId <= 0) return Results.BadRequest(new { error = "chainId must be > 0" });
            if (string.IsNullOrWhiteSpace(req.Seller) || string.IsNullOrWhiteSpace(req.WcBaseUrl) ||
                string.IsNullOrWhiteSpace(req.WcConsumerKey) || string.IsNullOrWhiteSpace(req.WcConsumerSecret))
            {
                return Results.BadRequest(new { error = "seller, wcBaseUrl, wcConsumerKey, wcConsumerSecret are required" });
            }

            string seller = req.Seller.Trim().ToLowerInvariant();
            string baseUrl = req.WcBaseUrl.Trim().TrimEnd('/');
            string consumerKey = req.WcConsumerKey.Trim();
            string consumerSecret = req.WcConsumerSecret.Trim();
            int timeout = Math.Clamp(req.TimeoutMs, 1000, 300000);

            await using var conn = new NpgsqlConnection(postgresConn);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO wc_connections (
                    chain_id, seller_address, wc_base_url, wc_consumer_key, wc_consumer_secret,
                    default_customer_id, order_status, timeout_ms, fulfill_inherit_request_abort,
                    enabled, created_at, revoked_at
                ) VALUES (
                    $1, $2, $3, $4, $5,
                    $6, $7, $8, $9,
                    $10, now(), CASE WHEN $10 THEN NULL ELSE now() END
                )
                ON CONFLICT (chain_id, seller_address) WHERE revoked_at IS NULL DO UPDATE SET
                    wc_base_url = EXCLUDED.wc_base_url,
                    wc_consumer_key = EXCLUDED.wc_consumer_key,
                    wc_consumer_secret = EXCLUDED.wc_consumer_secret,
                    default_customer_id = EXCLUDED.default_customer_id,
                    order_status = EXCLUDED.order_status,
                    timeout_ms = EXCLUDED.timeout_ms,
                    fulfill_inherit_request_abort = EXCLUDED.fulfill_inherit_request_abort,
                    enabled = EXCLUDED.enabled,
                    revoked_at = CASE WHEN EXCLUDED.enabled THEN NULL ELSE now() END
                """;
            cmd.Parameters.AddWithValue(req.ChainId);
            cmd.Parameters.AddWithValue(seller);
            cmd.Parameters.AddWithValue(baseUrl);
            cmd.Parameters.AddWithValue(consumerKey);
            cmd.Parameters.AddWithValue(consumerSecret);
            cmd.Parameters.AddWithValue((object?)req.DefaultCustomerId ?? DBNull.Value);
            cmd.Parameters.AddWithValue(req.OrderStatus.Trim());
            cmd.Parameters.AddWithValue(timeout);
            cmd.Parameters.AddWithValue(req.FulfillInheritRequestAbort);
            cmd.Parameters.AddWithValue(req.Enabled);
            await cmd.ExecuteNonQueryAsync(ct);

            return Results.Json(new WooCommerceConnectionDto
            {
                ChainId = req.ChainId,
                Seller = seller,
                WcBaseUrl = baseUrl,
                WcConsumerKey = Mask(consumerKey),
                DefaultCustomerId = req.DefaultCustomerId,
                OrderStatus = req.OrderStatus,
                TimeoutMs = timeout,
                FulfillInheritRequestAbort = req.FulfillInheritRequestAbort,
                Enabled = req.Enabled,
                RevokedAt = req.Enabled ? null : DateTimeOffset.UtcNow
            });
        });

        group.MapDelete("/connections/{chainId:long}/{seller}", async (long chainId, string seller, CancellationToken ct) =>
        {
            if (chainId <= 0) return Results.BadRequest(new { error = "chainId must be > 0" });
            if (string.IsNullOrWhiteSpace(seller)) return Results.BadRequest(new { error = "seller is required" });
            string sellerNorm = seller.Trim().ToLowerInvariant();

            await using var conn = new NpgsqlConnection(postgresConn);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE wc_connections SET enabled = false, revoked_at = now()
                WHERE chain_id = $1 AND seller_address = $2
                """;
            cmd.Parameters.AddWithValue(chainId);
            cmd.Parameters.AddWithValue(sellerNorm);
            var rows = await cmd.ExecuteNonQueryAsync(ct);
            if (rows == 0) return Results.NotFound(new { error = "connection not found" });
            return Results.Json(new { ok = true });
        });

        // ── Product Mappings ────────────────────────────────────────────────────

        group.MapGet("/mappings", async (CancellationToken ct) =>
        {
            var result = new List<ProductMappingDto>();
            await using var conn = new NpgsqlConnection(postgresConn);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT chain_id, seller_address, sku, wc_product_sku, wc_product_id, enabled, revoked_at
                FROM wc_product_mappings
                ORDER BY chain_id, seller_address, sku
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                result.Add(new ProductMappingDto
                {
                    ChainId = reader.GetInt64(0),
                    Seller = reader.GetString(1),
                    Sku = reader.GetString(2),
                    WcProductSku = reader.GetString(3),
                    WcProductId = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    Enabled = reader.GetBoolean(5),
                    RevokedAt = reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset?>(6)
                });
            }
            return Results.Json(result);
        });

        group.MapPut("/mappings", async (ProductMappingUpsertRequest req, CancellationToken ct) =>
        {
            if (req.ChainId <= 0) return Results.BadRequest(new { error = "chainId must be > 0" });
            if (string.IsNullOrWhiteSpace(req.Seller) || string.IsNullOrWhiteSpace(req.Sku) ||
                string.IsNullOrWhiteSpace(req.WcProductSku))
            {
                return Results.BadRequest(new { error = "seller, sku, wcProductSku are required" });
            }

            string seller = req.Seller.Trim().ToLowerInvariant();
            string sku = req.Sku.Trim();
            string wcSku = req.WcProductSku.Trim();

            await using var conn = new NpgsqlConnection(postgresConn);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO wc_product_mappings (
                    chain_id, seller_address, sku, wc_product_sku, wc_product_id,
                    enabled, created_at, revoked_at
                ) VALUES ($1, $2, $3, $4, $5, $6, now(),
                    CASE WHEN $6 THEN NULL ELSE now() END
                )
                ON CONFLICT (chain_id, seller_address, sku) WHERE revoked_at IS NULL DO UPDATE SET
                    wc_product_sku = EXCLUDED.wc_product_sku,
                    wc_product_id = EXCLUDED.wc_product_id,
                    enabled = EXCLUDED.enabled,
                    revoked_at = CASE WHEN EXCLUDED.enabled THEN NULL ELSE now() END
                """;
            cmd.Parameters.AddWithValue(req.ChainId);
            cmd.Parameters.AddWithValue(seller);
            cmd.Parameters.AddWithValue(sku);
            cmd.Parameters.AddWithValue(wcSku);
            cmd.Parameters.AddWithValue((object?)req.WcProductId ?? DBNull.Value);
            cmd.Parameters.AddWithValue(req.Enabled);
            await cmd.ExecuteNonQueryAsync(ct);

            return Results.Json(new ProductMappingDto
            {
                ChainId = req.ChainId,
                Seller = seller,
                Sku = sku,
                WcProductSku = wcSku,
                WcProductId = req.WcProductId,
                Enabled = req.Enabled,
                RevokedAt = req.Enabled ? null : DateTimeOffset.UtcNow
            });
        });

        group.MapDelete("/mappings/{chainId:long}/{seller}/{sku}", async (
            long chainId, string seller, string sku, CancellationToken ct) =>
        {
            if (chainId <= 0) return Results.BadRequest(new { error = "chainId must be > 0" });
            if (string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(sku))
                return Results.BadRequest(new { error = "seller and sku are required" });

            string sellerNorm = seller.Trim().ToLowerInvariant();
            string skuNorm = sku.Trim();

            await using var conn = new NpgsqlConnection(postgresConn);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE wc_product_mappings SET enabled = false, revoked_at = now()
                WHERE chain_id = $1 AND seller_address = $2 AND sku = $3
                """;
            cmd.Parameters.AddWithValue(chainId);
            cmd.Parameters.AddWithValue(sellerNorm);
            cmd.Parameters.AddWithValue(skuNorm);
            var rows = await cmd.ExecuteNonQueryAsync(ct);
            if (rows == 0) return Results.NotFound(new { error = "mapping not found" });
            return Results.Json(new { ok = true });
        });

        // ── Inventory Stock ─────────────────────────────────────────────────────

        group.MapGet("/stock/{chainId:long}/{seller}/{sku}", async (
            long chainId, string seller, string sku, CancellationToken ct) =>
        {
            if (chainId <= 0) return Results.BadRequest(new { error = "chainId must be > 0" });
            if (string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(sku))
                return Results.BadRequest(new { error = "seller and sku are required" });

            string sellerNorm = seller.Trim().ToLowerInvariant();
            string skuNorm = sku.Trim();

            await using var conn = new NpgsqlConnection(postgresConn);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT stock_quantity, updated_at
                FROM wc_inventory_stock
                WHERE chain_id = $1 AND seller_address = $2 AND sku = $3
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue(chainId);
            cmd.Parameters.AddWithValue(sellerNorm);
            cmd.Parameters.AddWithValue(skuNorm);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return Results.NotFound(new { error = "stock not configured" });

            return Results.Json(new
            {
                chainId,
                seller = sellerNorm,
                sku = skuNorm,
                stockQuantity = reader.GetInt32(0),
                updatedAt = reader.GetFieldValue<DateTimeOffset>(1)
            });
        });

        group.MapPut("/stock", async (StockUpsertRequest req, CancellationToken ct) =>
        {
            if (req.ChainId <= 0) return Results.BadRequest(new { error = "chainId must be > 0" });
            if (string.IsNullOrWhiteSpace(req.Seller) || string.IsNullOrWhiteSpace(req.Sku))
                return Results.BadRequest(new { error = "seller and sku are required" });
            // -1 means unlimited, so only reject if < -1
            if (req.StockQuantity < -1) return Results.BadRequest(new { error = "stockQuantity must be >= -1" });

            string seller = req.Seller.Trim().ToLowerInvariant();
            string sku = req.Sku.Trim();

            await using var conn = new NpgsqlConnection(postgresConn);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO wc_inventory_stock(id, chain_id, seller_address, sku, stock_quantity, updated_at)
                VALUES (gen_random_uuid(), $1, $2, $3, $4, now())
                ON CONFLICT (chain_id, seller_address, sku) DO UPDATE SET
                    stock_quantity = EXCLUDED.stock_quantity,
                    updated_at = now()
                """;
            cmd.Parameters.AddWithValue(req.ChainId);
            cmd.Parameters.AddWithValue(seller);
            cmd.Parameters.AddWithValue(sku);
            cmd.Parameters.AddWithValue(req.StockQuantity);
            await cmd.ExecuteNonQueryAsync(ct);

            return Results.Json(new { chainId = req.ChainId, seller, sku, stockQuantity = req.StockQuantity });
        });

        // ── Products Proxy (fetch from WooCommerce via stored credentials) ──────

        group.MapGet("/products/{chainId:long}/{seller}", async (
            HttpContext context,
            long chainId,
            string seller,
            IWooCommerceConnectionResolver connResolver,
            IProductMappingResolver mappingResolver,
            IHttpClientFactory httpFactory,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (chainId <= 0) return Results.BadRequest(new { error = "chainId must be > 0" });
            if (string.IsNullOrWhiteSpace(seller)) return Results.BadRequest(new { error = "seller is required" });

            string sellerNorm = seller.Trim().ToLowerInvariant();

            // Pagination
            if (!context.Request.Query.TryGetValue("limit", out var limitRaw) || !int.TryParse(limitRaw, out var limit))
                limit = 100;
            if (!context.Request.Query.TryGetValue("offset", out var offsetRaw) || !int.TryParse(offsetRaw, out var offset))
                offset = 0;
            if (!context.Request.Query.TryGetValue("sku", out var skuFilter))
                skuFilter = "";

            limit = Math.Clamp(limit, 1, 100);
            offset = Math.Max(0, offset);

            var wcConn = await connResolver.ResolveAsync(chainId, sellerNorm, ct);
            if (wcConn == null)
                return Results.NotFound(new { error = "No WooCommerce connection configured for this seller/chain." });

            var settings = new WooCommerceSettings
            {
                BaseUrl = wcConn.BaseUrl,
                ConsumerKey = wcConn.ConsumerKey,
                ConsumerSecret = wcConn.ConsumerSecret,
                TimeoutMs = wcConn.TimeoutMs
            };

            using var http = httpFactory.CreateClient();
            var client = new WooCommerceClient(http, settings, loggerFactory.CreateLogger<WooCommerceClient>());

            try
            {
                var products = await client.ListProductsAsync(
                    string.IsNullOrWhiteSpace(skuFilter) ? null : skuFilter.ToString(),
                    limit, offset, ct);

                return Results.Json(new
                {
                    items = products.Select(p => new
                    {
                        p.Id,
                        p.Name,
                        p.Sku,
                        p.Status,
                        p.Price,
                        p.StockQuantity,
                        p.StockStatus,
                        p.RegularPrice,
                        p.Description,
                        p.Permalink,
                        p.Categories
                    }),
                    limit,
                    offset,
                    sku = skuFilter.ToString()
                });
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger("AdminProducts").LogError(ex,
                    "Failed to list WooCommerce products for seller={Seller} chain={Chain}", sellerNorm, chainId);
                return Results.Json(new { error = "WooCommerce API error", details = ex.Message }, statusCode: 502);
            }
        });

        // ── Fulfillment Runs ───────────────────────────────────────────────────

        group.MapGet("/runs", async (CancellationToken ct) =>
        {
            await using var conn = new NpgsqlConnection(postgresConn);
            await conn.OpenAsync(ct);

            // Check if table exists first
            await using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = "SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_name = 'wc_fulfillment_runs')";
            if (!(bool)(await checkCmd.ExecuteScalarAsync(ct))!)
                return Results.Json(Array.Empty<FulfillmentRunDto>());

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, chain_id, seller_address, payment_reference, idempotency_key,
                       wc_order_id, wc_order_number, status, outcome, error_detail, created_at, completed_at
                FROM wc_fulfillment_runs
                ORDER BY created_at DESC
                LIMIT 200
                """;
            var result = new List<FulfillmentRunDto>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                result.Add(new FulfillmentRunDto
                {
                    Id = reader.GetGuid(0),
                    ChainId = reader.GetInt64(1),
                    Seller = reader.GetString(2),
                    PaymentReference = reader.GetString(3),
                    IdempotencyKey = reader.GetGuid(4),
                    WcOrderId = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    WcOrderNumber = reader.IsDBNull(6) ? null : reader.GetString(6),
                    Status = reader.GetString(7),
                    Outcome = reader.IsDBNull(8) ? null : reader.GetString(8),
                    ErrorDetail = reader.IsDBNull(9) ? null : reader.GetString(9),
                    CreatedAt = reader.GetFieldValue<DateTimeOffset>(10),
                    CompletedAt = reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset?>(11)
                });
            }
            return Results.Json(result);
        });
    }

    private static string Mask(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 6) return "***";
        return value[..2] + new string('*', value.Length - 4) + value[^2..];
    }
}