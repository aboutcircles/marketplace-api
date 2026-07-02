using System.Text.Json;
using Npgsql;
using Circles.Market.Fulfillment.Core;

namespace Circles.Market.Adapters.WooCommerce.Db;

/// <summary>
/// WooCommerce-specific fulfillment run store. Mirrors the Odoo pattern but uses
/// <c>wc_fulfillment_runs</c> with an explicit <c>idempotency_key</c> (UUID).
/// </summary>
public interface IWooCommerceFulfillmentRunStore : IFulfillmentRunStore
{
    /// <summary>Sets WC order info on a completed run.</summary>
    Task SetOrderInfoAsync(Guid runId, int wcOrderId, string wcOrderNumber, CancellationToken ct);

    /// <summary>Looks up a run by its idempotency key.</summary>
    Task<WooCommerceFulfillmentRunRecord?> GetByIdempotencyKeyAsync(Guid key, CancellationToken ct);

    /// <summary>Creates a new run record and returns its ID, or returns existing ID if key exists.</summary>
    Task<Guid> TryInsertAsync(long chainId, string seller, string paymentReference, Guid idempotencyKey,
        string requestPayload, CancellationToken ct);
}

public sealed class PostgresWooCommerceFulfillmentRunStore : IWooCommerceFulfillmentRunStore
{
    private readonly string _connString;
    private readonly ILogger<PostgresWooCommerceFulfillmentRunStore> _log;

    private static string Norm(string s) => string.IsNullOrWhiteSpace(s) ? throw new ArgumentException("Value cannot be null or whitespace.", nameof(s)) : s.Trim().ToLowerInvariant();
    private static string NormRef(string s) => string.IsNullOrWhiteSpace(s) ? throw new ArgumentException("Value cannot be null or whitespace.", nameof(s)) : s.Trim();

    public PostgresWooCommerceFulfillmentRunStore(string connString, ILogger<PostgresWooCommerceFulfillmentRunStore> log)
    {
        _connString = connString ?? throw new ArgumentNullException(nameof(connString));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    // Terminal-state bookkeeping (completed / failed / order-info) MUST complete even when the
    // fulfillment job itself was cancelled. The caller's CancellationToken is typically linked to
    // the fulfillment timeout and/or app-shutdown — i.e. the very cancellation that just aborted
    // the work. Honoring it here would abort the status transition and strand the run, blocking
    // re-drive. So these short, single-statement writes run under an independent, bounded token.
    private static readonly TimeSpan TerminalWriteTimeout = TimeSpan.FromSeconds(15);

    private async Task ExecuteTerminalWriteAsync(string sql, Action<NpgsqlParameterCollection> bindParameters)
    {
        using var writeCts = new CancellationTokenSource(TerminalWriteTimeout);
        CancellationToken writeCt = writeCts.Token;

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(writeCt);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        bindParameters(cmd.Parameters);
        await cmd.ExecuteNonQueryAsync(writeCt);
    }

    public async Task<(bool acquired, string? status)> TryBeginAsync(
        long chainId, string seller, string paymentReference, string orderId, CancellationToken ct)
    {
        // WooCommerce uses explicit idempotency keys from the request body.
        // This method is kept for interface compatibility but the real idempotency
        // is driven by TryInsertAsync + idempotency_key UUID.
        string sellerNorm = Norm(seller);
        string paymentNorm = NormRef(paymentReference);

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);

        // Use payment_reference as natural key for legacy compatibility
        const string insertSql = """
            INSERT INTO wc_fulfillment_runs(id, chain_id, seller_address, payment_reference, idempotency_key, status, request_payload)
            VALUES (gen_random_uuid(), @c, @s, @p, gen_random_uuid(), 'pending', '{}'::jsonb)
            ON CONFLICT (chain_id, seller_address, payment_reference) DO NOTHING
            RETURNING id;
            """;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = insertSql;
        cmd.Parameters.AddWithValue("@c", chainId);
        cmd.Parameters.AddWithValue("@s", sellerNorm);
        cmd.Parameters.AddWithValue("@p", paymentNorm);

        var result = await cmd.ExecuteScalarAsync(ct);
        if (result != null)
        {
            return (true, "pending");
        }

        string? status = await GetStatusAsync(chainId, sellerNorm, paymentNorm, ct);
        return (false, status);
    }

    public async Task<Guid> TryInsertAsync(
        long chainId, string seller, string paymentReference, Guid idempotencyKey,
        string requestPayload, CancellationToken ct)
    {
        string sellerNorm = Norm(seller);
        string paymentNorm = NormRef(paymentReference);

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);

        const string sql = """
            INSERT INTO wc_fulfillment_runs(id, chain_id, seller_address, payment_reference, idempotency_key, status, request_payload)
            VALUES (@id, @c, @s, @p, @key, 'pending', @payload::jsonb)
            ON CONFLICT (idempotency_key) DO UPDATE SET id = wc_fulfillment_runs.id
            RETURNING id;
            """;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@id", idempotencyKey);
        cmd.Parameters.AddWithValue("@c", chainId);
        cmd.Parameters.AddWithValue("@s", sellerNorm);
        cmd.Parameters.AddWithValue("@p", paymentNorm);
        cmd.Parameters.AddWithValue("@key", idempotencyKey);
        cmd.Parameters.AddWithValue("@payload", requestPayload);

        try
        {
            var result = await cmd.ExecuteScalarAsync(ct);
            return (Guid)result!;
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            // Hit the unique constraint on (chain_id, seller_address, payment_reference).
            // This means a run already exists for this payment — look it up and return its ID.
            _log.LogInformation("Fulfillment run already exists for payment_reference={PaymentRef}, returning existing run", paymentNorm);
            const string lookupSql = """
                SELECT id FROM wc_fulfillment_runs
                WHERE chain_id = @c AND seller_address = @s AND payment_reference = @p
                LIMIT 1;
                """;
            await using var lookupCmd = conn.CreateCommand();
            lookupCmd.CommandText = lookupSql;
            lookupCmd.Parameters.AddWithValue("@c", chainId);
            lookupCmd.Parameters.AddWithValue("@s", sellerNorm);
            lookupCmd.Parameters.AddWithValue("@p", paymentNorm);
            var existing = await lookupCmd.ExecuteScalarAsync(ct);
            return (Guid)existing!;
        }
    }

    public async Task<WooCommerceFulfillmentRunRecord?> GetByIdempotencyKeyAsync(Guid key, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);

        const string sql = """
            SELECT id, chain_id, seller_address, payment_reference, idempotency_key,
                   wc_order_id, wc_order_number, status, outcome, error_detail,
                   request_payload, response_payload, created_at, completed_at
            FROM wc_fulfillment_runs
            WHERE idempotency_key = @key
            LIMIT 1;
            """;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@key", key);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new WooCommerceFulfillmentRunRecord
        {
            Id = reader.GetGuid(0),
            ChainId = reader.GetInt64(1),
            SellerAddress = reader.GetString(2),
            PaymentReference = reader.GetString(3),
            IdempotencyKey = reader.GetGuid(4),
            WcOrderId = reader.IsDBNull(5) ? null : reader.GetInt32(5),
            WcOrderNumber = reader.IsDBNull(6) ? null : reader.GetString(6),
            Status = reader.GetString(7),
            Outcome = reader.IsDBNull(8) ? null : reader.GetString(8),
            ErrorDetail = reader.IsDBNull(9) ? null : reader.GetString(9),
            RequestPayload = reader.GetString(10),
            ResponsePayload = reader.IsDBNull(11) ? null : reader.GetString(11),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(12),
            CompletedAt = reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13)
        };
    }

    // ct intentionally not threaded into the terminal write — see ExecuteTerminalWriteAsync.
    public Task MarkOkAsync(long chainId, string seller, string paymentReference, CancellationToken ct)
    {
        string sellerNorm = Norm(seller);
        string paymentNorm = NormRef(paymentReference);

        const string sql = """
            UPDATE wc_fulfillment_runs
            SET status = 'completed', outcome = 'success', completed_at = now(), error_detail = NULL
            WHERE chain_id = @c AND seller_address = @s AND payment_reference = @p;
            """;
        return ExecuteTerminalWriteAsync(sql, p =>
        {
            p.AddWithValue("@c", chainId);
            p.AddWithValue("@s", sellerNorm);
            p.AddWithValue("@p", paymentNorm);
        });
    }

    // ct intentionally not threaded into the terminal write — see ExecuteTerminalWriteAsync.
    public Task MarkErrorAsync(long chainId, string seller, string paymentReference, string error, CancellationToken ct)
    {
        string sellerNorm = Norm(seller);
        string paymentNorm = NormRef(paymentReference);

        const string sql = """
            UPDATE wc_fulfillment_runs
            SET status = 'failed', outcome = 'wc_api_error', completed_at = now(), error_detail = @e
            WHERE chain_id = @c AND seller_address = @s AND payment_reference = @p;
            """;
        string errTrimmed = error.Length > 2000 ? error[..2000] : error;
        return ExecuteTerminalWriteAsync(sql, p =>
        {
            p.AddWithValue("@c", chainId);
            p.AddWithValue("@s", sellerNorm);
            p.AddWithValue("@p", paymentNorm);
            p.AddWithValue("@e", errTrimmed);
        });
    }

    // ct intentionally not threaded into the terminal write — see ExecuteTerminalWriteAsync.
    public Task SetOrderInfoAsync(Guid runId, int wcOrderId, string wcOrderNumber, CancellationToken ct)
    {
        const string sql = """
            UPDATE wc_fulfillment_runs
            SET wc_order_id = @oid, wc_order_number = @num
            WHERE id = @id;
            """;
        return ExecuteTerminalWriteAsync(sql, p =>
        {
            p.AddWithValue("@id", runId);
            p.AddWithValue("@oid", wcOrderId);
            p.AddWithValue("@num", wcOrderNumber);
        });
    }

    public async Task<string?> GetStatusAsync(long chainId, string seller, string paymentReference, CancellationToken ct)
    {
        string sellerNorm = Norm(seller);
        string paymentNorm = NormRef(paymentReference);

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);

        const string sql = """
            SELECT status FROM wc_fulfillment_runs
            WHERE chain_id = @c AND seller_address = @s AND payment_reference = @p
            LIMIT 1;
            """;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@c", chainId);
        cmd.Parameters.AddWithValue("@s", sellerNorm);
        cmd.Parameters.AddWithValue("@p", paymentNorm);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result?.ToString();
    }
}