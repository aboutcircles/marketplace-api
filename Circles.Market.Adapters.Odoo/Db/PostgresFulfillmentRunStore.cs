using Npgsql;

namespace Circles.Market.Adapters.Odoo.Db;

public interface IFulfillmentRunStore
{
    Task<(bool acquired, string? status)> TryBeginAsync(long chainId, string seller, string paymentReference, string orderId, CancellationToken ct);
    Task MarkOkAsync(long chainId, string seller, string paymentReference, CancellationToken ct);
    Task MarkErrorAsync(long chainId, string seller, string paymentReference, string error, CancellationToken ct);
    Task SetOdooOrderInfoAsync(long chainId, string seller, string paymentReference, int odooOrderId, string odooOrderName, CancellationToken ct);
    Task<string?> GetStatusAsync(long chainId, string seller, string paymentReference, CancellationToken ct);
}

public sealed class PostgresFulfillmentRunStore : IFulfillmentRunStore
{
    private readonly string _connString;
    private readonly ILogger<PostgresFulfillmentRunStore> _log;

    public PostgresFulfillmentRunStore(string connString, ILogger<PostgresFulfillmentRunStore> log)
    {
        _connString = connString ?? throw new ArgumentNullException(nameof(connString));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<(bool acquired, string? status)> TryBeginAsync(
        long chainId,
        string seller,
        string paymentReference,
        string orderId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(seller)) throw new ArgumentException("seller is required", nameof(seller));
        if (string.IsNullOrWhiteSpace(paymentReference)) throw new ArgumentException("paymentReference is required", nameof(paymentReference));
        if (string.IsNullOrWhiteSpace(orderId)) throw new ArgumentException("orderId is required", nameof(orderId));

        string sellerNorm = seller.Trim().ToLowerInvariant();
        string paymentNorm = paymentReference.Trim();
        string orderNorm = orderId.Trim();

        int staleMinutes = 10;
        string? staleEnv = Environment.GetEnvironmentVariable("ODOO_FULFILLMENT_STALE_MINUTES");
        if (!string.IsNullOrWhiteSpace(staleEnv) && int.TryParse(staleEnv, out var parsed) && parsed > 0)
        {
            staleMinutes = parsed;
        }

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);

        // 1) Insert new run (fast path)
        const string insertSql = @"
INSERT INTO fulfillment_runs(chain_id, seller_address, payment_reference, order_id, status, updated_at)
VALUES (@c, @s, @p, @o, 'started', now())
ON CONFLICT (chain_id, seller_address, payment_reference) DO NOTHING;
";
        await using (var ins = conn.CreateCommand())
        {
            ins.CommandText = insertSql;
            ins.Parameters.AddWithValue("@c", chainId);
            ins.Parameters.AddWithValue("@s", sellerNorm);
            ins.Parameters.AddWithValue("@p", paymentNorm);
            ins.Parameters.AddWithValue("@o", orderNorm);

            int rows = await ins.ExecuteNonQueryAsync(ct);
            if (rows == 1)
            {
                return (true, "started");
            }
        }

        // 2) Takeover if existing is error OR started but stale
        const string takeoverSql = @"
UPDATE fulfillment_runs
SET status='started',
    updated_at=now(),
    completed_at=NULL,
    last_error=NULL,
    order_id=@o
WHERE chain_id=@c AND seller_address=@s AND payment_reference=@p
  AND (
      status='error'
      OR (status='started' AND updated_at < now() - (@staleMinutes || ' minutes')::interval)
  );
";
        await using (var take = conn.CreateCommand())
        {
            take.CommandText = takeoverSql;
            take.Parameters.AddWithValue("@c", chainId);
            take.Parameters.AddWithValue("@s", sellerNorm);
            take.Parameters.AddWithValue("@p", paymentNorm);
            take.Parameters.AddWithValue("@o", orderNorm);
            take.Parameters.AddWithValue("@staleMinutes", staleMinutes);

            int updated = await take.ExecuteNonQueryAsync(ct);
            if (updated == 1)
            {
                return (true, "started");
            }
        }

        // 3) Not acquired: report status
        string? status = await GetStatusAsync(chainId, sellerNorm, paymentNorm, ct);
        return (false, status);
    }

    public async Task MarkOkAsync(long chainId, string seller, string paymentReference, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);

        const string sql = @"
UPDATE fulfillment_runs
SET status='ok', updated_at=now(), completed_at=now(), last_error=NULL
WHERE chain_id=@c AND seller_address=@s AND payment_reference=@p;
";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@c", chainId);
        cmd.Parameters.AddWithValue("@s", seller);
        cmd.Parameters.AddWithValue("@p", paymentReference);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkErrorAsync(long chainId, string seller, string paymentReference, string error, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);

        const string sql = @"
UPDATE fulfillment_runs
SET status='error', updated_at=now(), completed_at=now(), last_error=@e
WHERE chain_id=@c AND seller_address=@s AND payment_reference=@p;
";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@c", chainId);
        cmd.Parameters.AddWithValue("@s", seller);
        cmd.Parameters.AddWithValue("@p", paymentReference);
        cmd.Parameters.AddWithValue("@e", error);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetOdooOrderInfoAsync(long chainId, string seller, string paymentReference, int odooOrderId, string odooOrderName, CancellationToken ct)
    {
        await using var conn = new Npgsql.NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);

        const string sql = @"
UPDATE fulfillment_runs
SET odoo_order_id=@oid, odoo_order_name=@oname, updated_at=now()
WHERE chain_id=@c AND seller_address=@s AND payment_reference=@p;
";
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@c", chainId);
        cmd.Parameters.AddWithValue("@s", seller);
        cmd.Parameters.AddWithValue("@p", paymentReference);
        cmd.Parameters.AddWithValue("@oid", odooOrderId);
        cmd.Parameters.AddWithValue("@oname", odooOrderName);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetStatusAsync(long chainId, string seller, string paymentReference, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);

        const string sql = @"
SELECT status
FROM fulfillment_runs
WHERE chain_id=@c AND seller_address=@s AND payment_reference=@p
LIMIT 1;
";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@c", chainId);
        cmd.Parameters.AddWithValue("@s", seller);
        cmd.Parameters.AddWithValue("@p", paymentReference);

        object? scalar = await cmd.ExecuteScalarAsync(ct);
        return scalar?.ToString();
    }
}