using Circles.Market.Fulfillment.Core;
using Npgsql;

namespace Circles.Market.Adapters.CodeDispenser;

public interface ICodeDispenserFulfillmentRunStore : IFulfillmentRunStore;

public sealed class PostgresCodeFulfillmentRunStore : ICodeDispenserFulfillmentRunStore
{
    private readonly string _connString;

    private static string NormalizeSeller(string seller)
    {
        if (string.IsNullOrWhiteSpace(seller)) throw new ArgumentException("seller is required", nameof(seller));
        return seller.Trim().ToLowerInvariant();
    }

    private static string NormalizePaymentReference(string paymentReference)
    {
        if (string.IsNullOrWhiteSpace(paymentReference)) throw new ArgumentException("paymentReference is required", nameof(paymentReference));
        return paymentReference.Trim();
    }

    public PostgresCodeFulfillmentRunStore(string connString)
    {
        _connString = connString ?? throw new ArgumentNullException(nameof(connString));
    }

    public async Task<(bool acquired, string? status)> TryBeginAsync(
        long chainId,
        string seller,
        string paymentReference,
        string orderId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(orderId)) throw new ArgumentException("orderId is required", nameof(orderId));

        string sellerNorm = NormalizeSeller(seller);
        string paymentNorm = NormalizePaymentReference(paymentReference);
        string orderNorm = orderId.Trim();

        int staleMinutes = 10;
        string? staleEnv = Environment.GetEnvironmentVariable("CODE_FULFILLMENT_STALE_MINUTES");
        if (!string.IsNullOrWhiteSpace(staleEnv) && int.TryParse(staleEnv, out var parsed) && parsed > 0)
        {
            staleMinutes = parsed;
        }

        bool allowStartedTakeover = false;
        string? allowStartedTakeoverEnv = Environment.GetEnvironmentVariable("CODE_FULFILLMENT_ALLOW_STARTED_TAKEOVER");
        if (!string.IsNullOrWhiteSpace(allowStartedTakeoverEnv) && bool.TryParse(allowStartedTakeoverEnv, out var parsedAllow))
        {
            allowStartedTakeover = parsedAllow;
        }

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);

        const string insertSql = @"
INSERT INTO code_fulfillment_runs(chain_id, seller_address, payment_reference, order_id, status, updated_at)
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

        const string takeoverSql = @"
UPDATE code_fulfillment_runs
SET status='started',
    updated_at=now(),
    completed_at=NULL,
    last_error=NULL,
    order_id=@o
WHERE chain_id=@c AND seller_address=@s AND payment_reference=@p
  AND (
      status='error'
      OR (@allowStartedTakeover AND status='started' AND updated_at < now() - (@staleMinutes || ' minutes')::interval)
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
            take.Parameters.AddWithValue("@allowStartedTakeover", allowStartedTakeover);

            int updated = await take.ExecuteNonQueryAsync(ct);
            if (updated == 1)
            {
                return (true, "started");
            }
        }

        string? status = await GetStatusAsync(chainId, sellerNorm, paymentNorm, ct);
        return (false, status);
    }

    public async Task MarkOkAsync(long chainId, string seller, string paymentReference, CancellationToken ct)
    {
        string sellerNorm = NormalizeSeller(seller);
        string paymentNorm = NormalizePaymentReference(paymentReference);

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);

        const string sql = @"
UPDATE code_fulfillment_runs
SET status='ok', updated_at=now(), completed_at=now(), last_error=NULL
WHERE chain_id=@c AND seller_address=@s AND payment_reference=@p;
";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@c", chainId);
        cmd.Parameters.AddWithValue("@s", sellerNorm);
        cmd.Parameters.AddWithValue("@p", paymentNorm);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkErrorAsync(long chainId, string seller, string paymentReference, string error, CancellationToken ct)
    {
        string sellerNorm = NormalizeSeller(seller);
        string paymentNorm = NormalizePaymentReference(paymentReference);

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);

        const string sql = @"
UPDATE code_fulfillment_runs
SET status='error', updated_at=now(), completed_at=now(), last_error=@e
WHERE chain_id=@c AND seller_address=@s AND payment_reference=@p;
";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@c", chainId);
        cmd.Parameters.AddWithValue("@s", sellerNorm);
        cmd.Parameters.AddWithValue("@p", paymentNorm);
        cmd.Parameters.AddWithValue("@e", error);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetStatusAsync(long chainId, string seller, string paymentReference, CancellationToken ct)
    {
        string sellerNorm = NormalizeSeller(seller);
        string paymentNorm = NormalizePaymentReference(paymentReference);

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);

        const string sql = @"
SELECT status
FROM code_fulfillment_runs
WHERE chain_id=@c AND seller_address=@s AND payment_reference=@p
LIMIT 1;
";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@c", chainId);
        cmd.Parameters.AddWithValue("@s", sellerNorm);
        cmd.Parameters.AddWithValue("@p", paymentNorm);

        object? scalar = await cmd.ExecuteScalarAsync(ct);
        return scalar?.ToString();
    }
}
