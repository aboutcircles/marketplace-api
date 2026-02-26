using Circles.Market.Adapters.Odoo.Admin;
using Circles.Market.Shared.Admin;
using Microsoft.AspNetCore.Authorization;
using Npgsql;

namespace Circles.Market.Adapters.Odoo.Admin;

public static class AdminEndpoints
{
    public static void MapOdooAdminApi(this WebApplication app, string adminBasePath, string postgresConn)
    {
        var group = app.MapGroup(adminBasePath).RequireAuthorization();

        group.MapGet("/health", () => Results.Json(new { ok = true }));

        group.MapGet("/connections", async (CancellationToken ct) =>
        {
            var result = new List<OdooConnectionDto>();
            await using var conn = new NpgsqlConnection(postgresConn);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT chain_id, seller_address, odoo_url, odoo_db, odoo_uid, sale_partner_id, jsonrpc_timeout_ms, fulfill_inherit_request_abort, enabled, revoked_at
FROM odoo_connections
ORDER BY chain_id, seller_address";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                result.Add(new OdooConnectionDto
                {
                    ChainId = reader.GetInt64(0),
                    Seller = reader.GetString(1),
                    OdooUrl = reader.GetString(2),
                    OdooDb = reader.GetString(3),
                    OdooUid = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    SalePartnerId = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    JsonrpcTimeoutMs = reader.GetInt32(6),
                    FulfillInheritRequestAbort = reader.GetBoolean(7),
                    Enabled = reader.GetBoolean(8),
                    RevokedAt = reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset?>(9)
                });
            }
            return Results.Json(result);
        });

        group.MapPut("/connections", async (OdooConnectionUpsertRequest req, CancellationToken ct) =>
        {
            if (req.ChainId <= 0) return Results.BadRequest(new { error = "chainId must be > 0" });
            if (string.IsNullOrWhiteSpace(req.Seller) || string.IsNullOrWhiteSpace(req.OdooUrl) ||
                string.IsNullOrWhiteSpace(req.OdooDb) || string.IsNullOrWhiteSpace(req.OdooKey))
            {
                return Results.BadRequest(new { error = "seller, odooUrl, odooDb, odooKey are required" });
            }

            string seller = req.Seller.Trim().ToLowerInvariant();
            string odooUrl = req.OdooUrl.Trim();
            string odooDb = req.OdooDb.Trim();
            string odooKey = req.OdooKey.Trim();
            int timeout = Math.Clamp(req.JsonrpcTimeoutMs, 1000, 300000);

            await using var conn = new NpgsqlConnection(postgresConn);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO odoo_connections (
  seller_address, chain_id, odoo_url, odoo_db, odoo_uid, odoo_key,
  sale_partner_id, jsonrpc_timeout_ms, fulfill_inherit_request_abort,
  enabled, created_at, revoked_at
) VALUES (
  $1, $2, $3, $4, $5, $6,
  $7, $8, $9,
  $10, now(), CASE WHEN $10 THEN NULL ELSE now() END
)
ON CONFLICT (seller_address, chain_id) DO UPDATE SET
  odoo_url = EXCLUDED.odoo_url,
  odoo_db = EXCLUDED.odoo_db,
  odoo_uid = EXCLUDED.odoo_uid,
  odoo_key = EXCLUDED.odoo_key,
  sale_partner_id = EXCLUDED.sale_partner_id,
  jsonrpc_timeout_ms = EXCLUDED.jsonrpc_timeout_ms,
  fulfill_inherit_request_abort = EXCLUDED.fulfill_inherit_request_abort,
  enabled = EXCLUDED.enabled,
  revoked_at = CASE WHEN EXCLUDED.enabled THEN NULL ELSE now() END";
            cmd.Parameters.AddWithValue(seller);
            cmd.Parameters.AddWithValue(req.ChainId);
            cmd.Parameters.AddWithValue(odooUrl);
            cmd.Parameters.AddWithValue(odooDb);
            cmd.Parameters.AddWithValue((object?)req.OdooUid ?? DBNull.Value);
            cmd.Parameters.AddWithValue(odooKey);
            cmd.Parameters.AddWithValue((object?)req.SalePartnerId ?? DBNull.Value);
            cmd.Parameters.AddWithValue(timeout);
            cmd.Parameters.AddWithValue(req.FulfillInheritRequestAbort);
            cmd.Parameters.AddWithValue(req.Enabled);
            await cmd.ExecuteNonQueryAsync(ct);

            return Results.Json(new OdooConnectionDto
            {
                ChainId = req.ChainId,
                Seller = seller,
                OdooUrl = odooUrl,
                OdooDb = odooDb,
                OdooUid = req.OdooUid,
                SalePartnerId = req.SalePartnerId,
                JsonrpcTimeoutMs = timeout,
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
            cmd.CommandText = @"UPDATE odoo_connections SET enabled=false, revoked_at=now()
WHERE chain_id=$1 AND seller_address=$2";
            cmd.Parameters.AddWithValue(chainId);
            cmd.Parameters.AddWithValue(sellerNorm);
            var rows = await cmd.ExecuteNonQueryAsync(ct);
            if (rows == 0)
                return Results.NotFound(new { error = "connection not found" });
            return Results.Json(new { ok = true });
        });

        group.MapGet("/mappings", async (CancellationToken ct) =>
        {
            var result = new List<InventoryMappingDto>();
            await using var conn = new NpgsqlConnection(postgresConn);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT m.chain_id, m.seller_address, m.sku, m.odoo_product_code, m.enabled, m.revoked_at, s.available_qty
FROM inventory_mappings m
LEFT JOIN inventory_stock s
  ON s.chain_id = m.chain_id
 AND s.seller_address = m.seller_address
 AND s.sku = m.sku
ORDER BY m.chain_id, m.seller_address, m.sku";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                result.Add(new InventoryMappingDto
                {
                    ChainId = reader.GetInt64(0),
                    Seller = reader.GetString(1),
                    Sku = reader.GetString(2),
                    OdooProductCode = reader.GetString(3),
                    Enabled = reader.GetBoolean(4),
                    RevokedAt = reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset?>(5),
                    LocalAvailableQty = reader.IsDBNull(6) ? null : reader.GetInt64(6)
                });
            }
            return Results.Json(result);
        });

        group.MapPut("/mappings", async (InventoryMappingUpsertRequest req, CancellationToken ct) =>
        {
            if (req.ChainId <= 0) return Results.BadRequest(new { error = "chainId must be > 0" });
            if (string.IsNullOrWhiteSpace(req.Seller) || string.IsNullOrWhiteSpace(req.Sku) || string.IsNullOrWhiteSpace(req.OdooProductCode))
                return Results.BadRequest(new { error = "seller, sku, odooProductCode are required" });

            string seller = req.Seller.Trim().ToLowerInvariant();
            string sku = req.Sku.Trim().ToLowerInvariant();
            string code = req.OdooProductCode.Trim();

            await using var conn = new NpgsqlConnection(postgresConn);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO inventory_mappings (seller_address, chain_id, sku, odoo_product_code, enabled, created_at, revoked_at)
VALUES ($1, $2, $3, $4, $5, now(), CASE WHEN $5 THEN NULL ELSE now() END)
ON CONFLICT (seller_address, chain_id, sku) DO UPDATE SET
  odoo_product_code = EXCLUDED.odoo_product_code,
  enabled = EXCLUDED.enabled,
  revoked_at = CASE WHEN EXCLUDED.enabled THEN NULL ELSE now() END";
            cmd.Parameters.AddWithValue(seller);
            cmd.Parameters.AddWithValue(req.ChainId);
            cmd.Parameters.AddWithValue(sku);
            cmd.Parameters.AddWithValue(code);
            cmd.Parameters.AddWithValue(req.Enabled);
            await cmd.ExecuteNonQueryAsync(ct);

            return Results.Json(new InventoryMappingDto
            {
                ChainId = req.ChainId,
                Seller = seller,
                Sku = sku,
                OdooProductCode = code,
                Enabled = req.Enabled,
                RevokedAt = req.Enabled ? null : DateTimeOffset.UtcNow,
                LocalAvailableQty = null
            });
        });

        group.MapGet("/stock/{chainId:long}/{seller}/{sku}", async (
            long chainId,
            string seller,
            string sku,
            CancellationToken ct) =>
        {
            if (chainId <= 0) return Results.BadRequest(new { error = "chainId must be > 0" });
            if (string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(sku))
                return Results.BadRequest(new { error = "seller and sku are required" });

            string sellerNorm = seller.Trim().ToLowerInvariant();
            string skuNorm = sku.Trim().ToLowerInvariant();

            await using var conn = new NpgsqlConnection(postgresConn);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT available_qty, updated_at, updated_by
FROM inventory_stock
WHERE chain_id=$1 AND seller_address=$2 AND sku=$3
LIMIT 1";
            cmd.Parameters.AddWithValue(chainId);
            cmd.Parameters.AddWithValue(sellerNorm);
            cmd.Parameters.AddWithValue(skuNorm);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return Results.NotFound(new { error = "stock not configured" });
            }

            return Results.Json(new
            {
                chainId,
                seller = sellerNorm,
                sku = skuNorm,
                availableQty = reader.GetInt64(0),
                updatedAt = reader.GetFieldValue<DateTimeOffset>(1),
                updatedBy = reader.IsDBNull(2) ? null : reader.GetString(2)
            });
        });

        group.MapPut("/stock", async (HttpContext context, InventoryStockUpsertRequest req, CancellationToken ct) =>
        {
            if (req.ChainId <= 0) return Results.BadRequest(new { error = "chainId must be > 0" });
            if (string.IsNullOrWhiteSpace(req.Seller) || string.IsNullOrWhiteSpace(req.Sku))
                return Results.BadRequest(new { error = "seller and sku are required" });
            if (req.AvailableQty < 0) return Results.BadRequest(new { error = "availableQty must be >= 0" });

            string seller = req.Seller.Trim().ToLowerInvariant();
            string sku = req.Sku.Trim().ToLowerInvariant();
            string? updatedBy = context.User.FindFirst("sub")?.Value
                                ?? context.User.Identity?.Name;

            await using var conn = new NpgsqlConnection(postgresConn);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO inventory_stock(chain_id, seller_address, sku, available_qty, updated_at, updated_by)
VALUES ($1, $2, $3, $4, now(), $5)
ON CONFLICT (chain_id, seller_address, sku)
DO UPDATE SET
  available_qty = EXCLUDED.available_qty,
  updated_at = now(),
  updated_by = EXCLUDED.updated_by";
            cmd.Parameters.AddWithValue(req.ChainId);
            cmd.Parameters.AddWithValue(seller);
            cmd.Parameters.AddWithValue(sku);
            cmd.Parameters.AddWithValue(req.AvailableQty);
            cmd.Parameters.AddWithValue((object?)updatedBy ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);

            return Results.Json(new
            {
                chainId = req.ChainId,
                seller,
                sku,
                availableQty = req.AvailableQty,
                updatedBy
            });
        });

        group.MapDelete("/mappings/{chainId:long}/{seller}/{sku}", async (long chainId, string seller, string sku, CancellationToken ct) =>
        {
            if (chainId <= 0) return Results.BadRequest(new { error = "chainId must be > 0" });
            if (string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(sku))
                return Results.BadRequest(new { error = "seller and sku are required" });

            string sellerNorm = seller.Trim().ToLowerInvariant();
            string skuNorm = sku.Trim().ToLowerInvariant();

            await using var conn = new NpgsqlConnection(postgresConn);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"UPDATE inventory_mappings SET enabled=false, revoked_at=now()
WHERE chain_id=$1 AND seller_address=$2 AND sku=$3";
            cmd.Parameters.AddWithValue(chainId);
            cmd.Parameters.AddWithValue(sellerNorm);
            cmd.Parameters.AddWithValue(skuNorm);
            var rows = await cmd.ExecuteNonQueryAsync(ct);
            if (rows == 0)
                return Results.NotFound(new { error = "mapping not found" });
            return Results.Json(new { ok = true });
        });

        group.MapGet("/products/{chainId:long}/{seller}", async (
            HttpContext context,
            long chainId,
            string seller,
            IOdooConnectionResolver resolver,
            IHttpClientFactory httpFactory,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (chainId <= 0) return Results.BadRequest(new { error = "chainId must be > 0" });
            if (string.IsNullOrWhiteSpace(seller)) return Results.BadRequest(new { error = "seller is required" });

            string sellerNorm = seller.Trim().ToLowerInvariant();
            if (!context.Request.Query.TryGetValue("limit", out var limitRaw) || !int.TryParse(limitRaw, out var limit))
            {
                limit = 100;
            }

            if (!context.Request.Query.TryGetValue("offset", out var offsetRaw) || !int.TryParse(offsetRaw, out var offset))
            {
                offset = 0;
            }

            if (limit < 1) limit = 1;
            if (limit > 500) limit = 500;
            if (offset < 0) offset = 0;

            bool activeOnly = false;
            if (context.Request.Query.TryGetValue("activeOnly", out var activeRaw))
            {
                bool.TryParse(activeRaw, out activeOnly);
            }

            bool hasCode = false;
            if (context.Request.Query.TryGetValue("hasCode", out var codeRaw))
            {
                bool.TryParse(codeRaw, out hasCode);
            }

            var settings = await resolver.ResolveAsync(chainId, sellerNorm, ct);
            if (settings == null)
            {
                return Results.NotFound(new { error = "No Odoo connection configured for this seller/chain." });
            }

            var http = httpFactory.CreateClient();
            var client = new OdooClient(http, settings, loggerFactory.CreateLogger<OdooClient>());
            await client.UpdateBaseAddressAsync(ct);

            OdooProductVariantListItemDto[] items;
            try
            {
                items = await client.ListProductVariantsAsync(activeOnly, hasCode, limit, offset, ct);
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger("AdminProducts").LogError(ex, "Failed to list products for seller={Seller} chain={Chain}", sellerNorm, chainId);
                return Results.Json(new { error = "Upstream auth failed or other error", details = ex.Message }, statusCode: 502);
            }

            var payload = new OdooProductVariantQueryResult
            {
                Items = items?.ToList() ?? new List<OdooProductVariantListItemDto>(),
                Limit = limit,
                Offset = offset,
                ActiveOnly = activeOnly,
                HasCode = hasCode
            };

            return Results.Json(payload);
        });
    }
}
