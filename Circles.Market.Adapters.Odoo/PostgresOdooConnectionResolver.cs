using Npgsql;

namespace Circles.Market.Adapters.Odoo;

public interface IOdooConnectionResolver
{
    Task<OdooSettings?> ResolveAsync(long chainId, string sellerAddress, CancellationToken ct);
}

public sealed class PostgresOdooConnectionResolver : IOdooConnectionResolver
{
    private readonly string _connString;
    private readonly ILogger<PostgresOdooConnectionResolver> _log;

    public PostgresOdooConnectionResolver(string connString, ILogger<PostgresOdooConnectionResolver> log)
    {
        _connString = connString ?? throw new ArgumentNullException(nameof(connString));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<OdooSettings?> ResolveAsync(long chainId, string sellerAddress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sellerAddress)) return null;
        string sellerNorm = sellerAddress.Trim().ToLowerInvariant();

        try
        {
            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT odoo_url, odoo_db, odoo_key, odoo_uid, sale_partner_id, jsonrpc_timeout_ms, fulfill_inherit_request_abort
FROM odoo_connections
WHERE seller_address = $1 AND chain_id = $2 AND enabled = true AND revoked_at IS NULL";
            cmd.Parameters.AddWithValue(sellerNorm);
            cmd.Parameters.AddWithValue(chainId);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }

            string baseUrl = reader.GetString(0);
            string db = reader.GetString(1);
            string key = reader.GetString(2);
            int? uid = reader.IsDBNull(3) ? null : reader.GetInt32(3);
            int? partnerId = reader.IsDBNull(4) ? null : reader.GetInt32(4);

            int timeoutMs = reader.GetInt32(5);
            if (timeoutMs < 1000) timeoutMs = 1000;
            if (timeoutMs > 300000) timeoutMs = 300000;

            bool inheritAbort = reader.GetBoolean(6);

            if (!uid.HasValue || uid.Value <= 0)
            {
                _log.LogError("Odoo uid missing/invalid for seller={Seller} chain={Chain}", sellerNorm, chainId);
                return null;
            }

            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out _))
            {
                _log.LogError("Invalid Odoo base URL for seller={Seller} chain={Chain}", sellerNorm, chainId);
                return null;
            }

            // fail-closed for multiple rows
            if (await reader.ReadAsync(ct))
            {
                _log.LogError("Multiple Odoo connections found for seller={Seller} chain={Chain}", sellerNorm, chainId);
                return null;
            }

            return new OdooSettings
            {
                BaseUrl = baseUrl,
                Db = db,
                UserId = uid.Value,
                Key = key,
                SalePartnerId = partnerId,
                JsonRpcTimeoutMs = timeoutMs,
                FulfillInheritRequestAbort = inheritAbort
            };
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error resolving Odoo connection for seller={Seller} chain={Chain}", sellerNorm, chainId);
            return null;
        }
    }
}
