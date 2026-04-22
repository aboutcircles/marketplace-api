using Npgsql;
using Circles.Market.Adapters.WooCommerce.Db;

namespace Circles.Market.Adapters.WooCommerce;

/// <summary>
/// Resolves WooCommerce connection details for a seller from Postgres.
/// Mirrors <c>PostgresOdooConnectionResolver</c>.
/// </summary>
public interface IWooCommerceConnectionResolver
{
    Task<WooCommerceConnectionInfo?> ResolveAsync(long chainId, string seller, CancellationToken ct = default);
}

public sealed class PostgresWooCommerceConnectionResolver : IWooCommerceConnectionResolver
{
    private readonly string _connString;
    private readonly ILogger<PostgresWooCommerceConnectionResolver> _log;

    public PostgresWooCommerceConnectionResolver(string connString, ILogger<PostgresWooCommerceConnectionResolver> log)
    {
        _connString = connString ?? throw new ArgumentNullException(nameof(connString));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<WooCommerceConnectionInfo?> ResolveAsync(long chainId, string seller, CancellationToken ct)
    {
        string sellerNorm = seller.Trim().ToLowerInvariant();

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);

        const string sql = """
            SELECT wc_base_url, wc_consumer_key, wc_consumer_secret, default_customer_id,
                   order_status, timeout_ms, fulfill_inherit_request_abort
            FROM wc_connections
            WHERE chain_id = @c AND seller_address = @s AND enabled = true AND revoked_at IS NULL
            LIMIT 1;
            """;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@c", chainId);
        cmd.Parameters.AddWithValue("@s", sellerNorm);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            _log.LogWarning("No WooCommerce connection found for seller={Seller} chain={Chain}", seller, chainId);
            return null;
        }

        return new WooCommerceConnectionInfo
        {
            BaseUrl = reader.GetString(0),
            ConsumerKey = reader.GetString(1),
            ConsumerSecret = reader.GetString(2),
            DefaultCustomerId = reader.IsDBNull(3) ? null : reader.GetInt32(3),
            OrderStatus = reader.GetString(4),
            TimeoutMs = reader.GetInt32(5),
            InheritRequestAbort = reader.GetBoolean(6)
        };
    }
}