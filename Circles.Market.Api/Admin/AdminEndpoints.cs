using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Circles.Market.Shared.Admin;
using Microsoft.AspNetCore.Authorization;
using Npgsql;

namespace Circles.Market.Api.Admin;

public static class AdminEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static bool TryGetBearerAuthorization(HttpContext ctx, out AuthenticationHeaderValue header)
    {
        header = default!;
        if (!ctx.Request.Headers.TryGetValue("Authorization", out var raw))
        {
            return false;
        }

        var value = raw.ToString();
        if (!AuthenticationHeaderValue.TryParse(value, out var parsed) || parsed is null)
        {
            return false;
        }

        var isBearer = string.Equals(parsed.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase);
        var hasToken = !string.IsNullOrWhiteSpace(parsed.Parameter);
        if (!isBearer || !hasToken)
        {
            return false;
        }

        header = parsed;
        return true;
    }

    private static async Task<(bool ok, T? body)> TryGetJsonAsync<T>(HttpClient client, string path, CancellationToken ct)
    {
        using var response = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            return (false, default);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var body = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct);
        return (true, body);
    }

    public static void MapMarketAdminApi(this WebApplication app, string adminBasePath, string marketConn)
    {
        var group = app.MapGroup(adminBasePath).RequireAuthorization(new AuthorizeAttribute
        {
            AuthenticationSchemes = AdminAuthConstants.Scheme
        });

        group.MapGet("/health", () => Results.Json(new { ok = true }));

        group.MapGet("/routes", async (CancellationToken ct) =>
        {
            var result = new List<AdminRouteDto>();
            await using var conn = new NpgsqlConnection(marketConn);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT chain_id, seller_address, sku, offer_type, is_one_off, enabled
FROM market_service_routes
ORDER BY chain_id, seller_address, sku";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                result.Add(new AdminRouteDto
                {
                    ChainId = reader.GetInt64(0),
                    Seller = reader.GetString(1),
                    Sku = reader.GetString(2),
                    OfferType = reader.IsDBNull(3) ? null : reader.GetString(3),
                    IsOneOff = reader.GetBoolean(4),
                    Enabled = reader.GetBoolean(5)
                });
            }
            return Results.Json(result);
        });

        group.MapPut("/routes", async (UpsertRouteRequest req, CancellationToken ct) =>
        {
            if (req.ChainId <= 0) return Results.BadRequest(new { error = "chainId must be > 0" });
            if (string.IsNullOrWhiteSpace(req.Seller) || string.IsNullOrWhiteSpace(req.Sku))
                return Results.BadRequest(new { error = "seller and sku are required" });

            string seller = req.Seller.Trim().ToLowerInvariant();
            string sku = req.Sku.Trim().ToLowerInvariant();
            string? offerType = string.IsNullOrWhiteSpace(req.OfferType) ? null : req.OfferType.Trim().ToLowerInvariant();

            if (!req.IsOneOff && string.IsNullOrWhiteSpace(offerType))
                return Results.BadRequest(new { error = "offerType is required when isOneOff=false" });

            await using var conn = new NpgsqlConnection(marketConn);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO market_service_routes (chain_id, seller_address, sku, offer_type, is_one_off, enabled)
VALUES ($1, $2, $3, $4, $5, $6)
ON CONFLICT (chain_id, seller_address, sku) DO UPDATE SET
  offer_type = EXCLUDED.offer_type,
  is_one_off = EXCLUDED.is_one_off,
  enabled = EXCLUDED.enabled";
            cmd.Parameters.AddWithValue(req.ChainId);
            cmd.Parameters.AddWithValue(seller);
            cmd.Parameters.AddWithValue(sku);
            cmd.Parameters.AddWithValue((object?)offerType ?? DBNull.Value);
            cmd.Parameters.AddWithValue(req.IsOneOff);
            cmd.Parameters.AddWithValue(req.Enabled);

            try
            {
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
            {
                return Results.BadRequest(new { error = "unknown offerType" });
            }

            return Results.Json(new AdminRouteDto
            {
                ChainId = req.ChainId,
                Seller = seller,
                Sku = sku,
                OfferType = offerType,
                IsOneOff = req.IsOneOff,
                Enabled = req.Enabled
            });
        });

        group.MapDelete("/routes/{chainId:long}/{seller}/{sku}", async (long chainId, string seller, string sku, CancellationToken ct) =>
        {
            if (chainId <= 0) return Results.BadRequest(new { error = "chainId must be > 0" });
            if (string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(sku))
                return Results.BadRequest(new { error = "seller and sku are required" });

            string sellerNorm = seller.Trim().ToLowerInvariant();
            string skuNorm = sku.Trim().ToLowerInvariant();

            await using var conn = new NpgsqlConnection(marketConn);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"UPDATE market_service_routes SET enabled=false
WHERE chain_id=$1 AND seller_address=$2 AND sku=$3";
            cmd.Parameters.AddWithValue(chainId);
            cmd.Parameters.AddWithValue(sellerNorm);
            cmd.Parameters.AddWithValue(skuNorm);
            var rows = await cmd.ExecuteNonQueryAsync(ct);
            if (rows == 0) return Results.NotFound(new { error = "route not found" });
            return Results.Json(new { ok = true });
        });

        group.MapGet("/odoo-products", async (IHttpClientFactory httpClientFactory, CancellationToken ct) =>
        {
            var odooClient = httpClientFactory.CreateClient("odoo-admin");
            var (ok, mappings) = await TryGetJsonAsync<List<AdminOdooProductDto>>(odooClient, "/admin/mappings", ct);
            if (!ok) return Results.StatusCode(StatusCodes.Status502BadGateway);
            return Results.Json(mappings ?? new List<AdminOdooProductDto>());
        });

        group.MapGet("/code-products", async (IHttpClientFactory httpClientFactory, CancellationToken ct) =>
        {
            var codeClient = httpClientFactory.CreateClient("codedisp-admin");
            var (okMappings, mappings) = await TryGetJsonAsync<List<AdminCodeProductDto>>(codeClient, "/admin/mappings", ct);
            if (!okMappings) return Results.StatusCode(StatusCodes.Status502BadGateway);

            var (okPools, pools) = await TryGetJsonAsync<List<AdminCodePoolDto>>(codeClient, "/admin/code-pools", ct);
            if (!okPools) return Results.StatusCode(StatusCodes.Status502BadGateway);

            var remainingByPool = (pools ?? new List<AdminCodePoolDto>()).ToDictionary(p => p.PoolId, p => p.Remaining, StringComparer.Ordinal);
            if (mappings != null)
            {
                foreach (var m in mappings)
                {
                    if (remainingByPool.TryGetValue(m.PoolId, out var remaining))
                        m.PoolRemaining = remaining;
                }
            }

            return Results.Json(mappings ?? new List<AdminCodeProductDto>());
        });

        group.MapDelete("/odoo-products/{chainId:long}/{seller}/{sku}", async (HttpContext ctx, long chainId, string seller, string sku, IHttpClientFactory httpClientFactory, CancellationToken ct) =>
        {
            if (chainId <= 0) return Results.BadRequest(new { error = "chainId must be > 0" });
            if (string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(sku))
                return Results.BadRequest(new { error = "seller and sku are required" });
            if (!TryGetBearerAuthorization(ctx, out var bearerHeader))
                return Results.Json(new { error = "missing or invalid bearer token" }, statusCode: StatusCodes.Status401Unauthorized);

            string sellerNorm = seller.Trim().ToLowerInvariant();
            string skuNorm = sku.Trim().ToLowerInvariant();

            var odooClient = httpClientFactory.CreateClient("odoo-admin");
            using var req = new HttpRequestMessage(HttpMethod.Delete,
                $"/admin/mappings/{chainId}/{Uri.EscapeDataString(sellerNorm)}/{Uri.EscapeDataString(skuNorm)}");
            req.Headers.Authorization = bearerHeader;
            using var resp = await odooClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return Results.NotFound(new { error = "mapping not found" });
            if (!resp.IsSuccessStatusCode)
                return Results.StatusCode(StatusCodes.Status502BadGateway);

            await using var conn = new NpgsqlConnection(marketConn);
            await conn.OpenAsync(ct);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"UPDATE market_service_routes SET enabled=false
WHERE chain_id=$1 AND seller_address=$2 AND sku=$3";
                cmd.Parameters.AddWithValue(chainId);
                cmd.Parameters.AddWithValue(sellerNorm);
                cmd.Parameters.AddWithValue(skuNorm);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            return Results.Json(new { ok = true });
        });

        group.MapDelete("/code-products/{chainId:long}/{seller}/{sku}", async (HttpContext ctx, long chainId, string seller, string sku, IHttpClientFactory httpClientFactory, CancellationToken ct) =>
        {
            if (chainId <= 0) return Results.BadRequest(new { error = "chainId must be > 0" });
            if (string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(sku))
                return Results.BadRequest(new { error = "seller and sku are required" });
            if (!TryGetBearerAuthorization(ctx, out var bearerHeader))
                return Results.Json(new { error = "missing or invalid bearer token" }, statusCode: StatusCodes.Status401Unauthorized);

            string sellerNorm = seller.Trim().ToLowerInvariant();
            string skuNorm = sku.Trim().ToLowerInvariant();

            var codeClient = httpClientFactory.CreateClient("codedisp-admin");
            using var req = new HttpRequestMessage(HttpMethod.Delete,
                $"/admin/mappings/{chainId}/{Uri.EscapeDataString(sellerNorm)}/{Uri.EscapeDataString(skuNorm)}");
            req.Headers.Authorization = bearerHeader;
            using var resp = await codeClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return Results.NotFound(new { error = "mapping not found" });
            if (!resp.IsSuccessStatusCode)
                return Results.StatusCode(StatusCodes.Status502BadGateway);

            await using var conn = new NpgsqlConnection(marketConn);
            await conn.OpenAsync(ct);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"UPDATE market_service_routes SET enabled=false
WHERE chain_id=$1 AND seller_address=$2 AND sku=$3";
                cmd.Parameters.AddWithValue(chainId);
                cmd.Parameters.AddWithValue(sellerNorm);
                cmd.Parameters.AddWithValue(skuNorm);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            return Results.Json(new { ok = true });
        });

        group.MapPost("/odoo-products", async (HttpContext ctx, AddOdooProductRequest req, IHttpClientFactory httpClientFactory, CancellationToken ct) =>
        {
            if (req.ChainId <= 0) return Results.BadRequest(new { error = "chainId must be > 0" });
            if (string.IsNullOrWhiteSpace(req.Seller) || string.IsNullOrWhiteSpace(req.Sku) || string.IsNullOrWhiteSpace(req.OdooProductCode))
                return Results.BadRequest(new { error = "seller, sku, odooProductCode are required" });
            if (string.IsNullOrWhiteSpace(req.OdooUrl) || string.IsNullOrWhiteSpace(req.OdooDb) || string.IsNullOrWhiteSpace(req.OdooKey))
                return Results.BadRequest(new { error = "odooUrl, odooDb, odooKey are required" });

            if (!TryGetBearerAuthorization(ctx, out var bearerHeader))
                return Results.Json(new { error = "missing or invalid bearer token" }, statusCode: StatusCodes.Status401Unauthorized);

            string seller = req.Seller.Trim().ToLowerInvariant();
            string sku = req.Sku.Trim().ToLowerInvariant();
            string code = req.OdooProductCode.Trim();
            string odooUrl = req.OdooUrl.Trim();
            string odooDb = req.OdooDb.Trim();
            string odooKey = req.OdooKey.Trim();
            int timeout = Math.Clamp(req.JsonrpcTimeoutMs, 1000, 300000);

            var odooClient = httpClientFactory.CreateClient("odoo-admin");

            var connPayload = new
            {
                chainId = req.ChainId,
                seller,
                odooUrl,
                odooDb,
                odooUid = req.OdooUid,
                odooKey,
                salePartnerId = req.SalePartnerId,
                jsonrpcTimeoutMs = timeout,
                fulfillInheritRequestAbort = req.FulfillInheritRequestAbort,
                enabled = req.Enabled
            };

            using var connRequest = new HttpRequestMessage(HttpMethod.Put, "/admin/connections")
            {
                Content = new StringContent(JsonSerializer.Serialize(connPayload), Encoding.UTF8, "application/json")
            };
            connRequest.Headers.Authorization = bearerHeader;

            using var connResponse = await odooClient.SendAsync(connRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!connResponse.IsSuccessStatusCode)
            {
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }

            var mappingPayload = new
            {
                chainId = req.ChainId,
                seller,
                sku,
                odooProductCode = code,
                enabled = req.Enabled
            };
            using var mappingRequest = new HttpRequestMessage(HttpMethod.Put, "/admin/mappings")
            {
                Content = new StringContent(JsonSerializer.Serialize(mappingPayload), Encoding.UTF8, "application/json")
            };
            mappingRequest.Headers.Authorization = bearerHeader;

            using var mappingResponse = await odooClient.SendAsync(mappingRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!mappingResponse.IsSuccessStatusCode)
            {
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }

            await using var market = new NpgsqlConnection(marketConn);
            await market.OpenAsync(ct);
            await using (var cmd = market.CreateCommand())
            {
                cmd.CommandText = @"INSERT INTO market_service_routes (chain_id, seller_address, sku, offer_type, is_one_off, enabled)
VALUES ($1, $2, $3, 'odoo', false, $4)
ON CONFLICT (chain_id, seller_address, sku) DO UPDATE SET
  offer_type = EXCLUDED.offer_type,
  is_one_off = false,
  enabled = EXCLUDED.enabled";
                cmd.Parameters.AddWithValue(req.ChainId);
                cmd.Parameters.AddWithValue(seller);
                cmd.Parameters.AddWithValue(sku);
                cmd.Parameters.AddWithValue(req.Enabled);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            return Results.Json(new { ok = true, chainId = req.ChainId, seller, sku, offerType = "odoo" });
        });

        group.MapPost("/code-products", async (HttpContext ctx, AddCodeProductRequest req, IHttpClientFactory httpClientFactory, CancellationToken ct) =>
        {
            if (req.ChainId <= 0) return Results.BadRequest(new { error = "chainId must be > 0" });
            if (string.IsNullOrWhiteSpace(req.Seller) || string.IsNullOrWhiteSpace(req.Sku) || string.IsNullOrWhiteSpace(req.PoolId))
                return Results.BadRequest(new { error = "seller, sku, poolId are required" });

            if (!TryGetBearerAuthorization(ctx, out var bearerHeader))
                return Results.Json(new { error = "missing or invalid bearer token" }, statusCode: StatusCodes.Status401Unauthorized);

            string seller = req.Seller.Trim().ToLowerInvariant();
            string sku = req.Sku.Trim().ToLowerInvariant();
            string pool = req.PoolId.Trim();
            string? tmpl = string.IsNullOrWhiteSpace(req.DownloadUrlTemplate) ? null : req.DownloadUrlTemplate.Trim();

            var codeClient = httpClientFactory.CreateClient("codedisp-admin");

            using var ensurePoolRequest = new HttpRequestMessage(HttpMethod.Post, "/admin/code-pools")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { poolId = pool }), Encoding.UTF8, "application/json")
            };
            ensurePoolRequest.Headers.Authorization = bearerHeader;

            using var ensurePoolResponse = await codeClient.SendAsync(ensurePoolRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!ensurePoolResponse.IsSuccessStatusCode)
            {
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }

            if (req.Codes is { Count: > 0 })
            {
                var seedPayload = new { codes = req.Codes.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToList() };
                if (seedPayload.codes.Count > 0)
                {
                    using var seedRequest = new HttpRequestMessage(HttpMethod.Post, $"/admin/code-pools/{Uri.EscapeDataString(pool)}/seed")
                    {
                        Content = new StringContent(JsonSerializer.Serialize(seedPayload), Encoding.UTF8, "application/json")
                    };
                    seedRequest.Headers.Authorization = bearerHeader;
                    using var seedResponse = await codeClient.SendAsync(seedRequest, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (!seedResponse.IsSuccessStatusCode)
                    {
                        return Results.StatusCode(StatusCodes.Status502BadGateway);
                    }
                }
            }

            var mappingPayload = new
            {
                chainId = req.ChainId,
                seller,
                sku,
                poolId = pool,
                downloadUrlTemplate = tmpl,
                enabled = req.Enabled
            };
            using var mappingRequest = new HttpRequestMessage(HttpMethod.Put, "/admin/mappings")
            {
                Content = new StringContent(JsonSerializer.Serialize(mappingPayload), Encoding.UTF8, "application/json")
            };
            mappingRequest.Headers.Authorization = bearerHeader;

            using var mappingResponse = await codeClient.SendAsync(mappingRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!mappingResponse.IsSuccessStatusCode)
            {
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }

            await using var market = new NpgsqlConnection(marketConn);
            await market.OpenAsync(ct);
            await using (var cmd = market.CreateCommand())
            {
                cmd.CommandText = @"INSERT INTO market_service_routes (chain_id, seller_address, sku, offer_type, is_one_off, enabled)
VALUES ($1, $2, $3, 'codedispenser', false, $4)
ON CONFLICT (chain_id, seller_address, sku) DO UPDATE SET
  offer_type = EXCLUDED.offer_type,
  is_one_off = false,
  enabled = EXCLUDED.enabled";
                cmd.Parameters.AddWithValue(req.ChainId);
                cmd.Parameters.AddWithValue(seller);
                cmd.Parameters.AddWithValue(sku);
                cmd.Parameters.AddWithValue(req.Enabled);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            return Results.Json(new { ok = true, chainId = req.ChainId, seller, sku, offerType = "codedispenser" });
        });
    }
}
