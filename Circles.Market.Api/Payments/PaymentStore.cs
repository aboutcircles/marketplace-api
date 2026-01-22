using Npgsql;
using System.Numerics;

namespace Circles.Market.Api.Payments;

public interface IPaymentStore
{
    // Upsert a single on-chain transfer (log-level).
    void UpsertObservedTransfer(PaymentTransferRecord transfer);

    // Recompute and persist the aggregated payment row for a payment_reference. Return row or null.
    PaymentRecord? UpsertAndGetPayment(long chainId, string paymentReference);

    // Mark a payment as confirmed.
    bool MarkConfirmed(long chainId, string paymentReference, long blockNumber, DateTimeOffset confirmedAt);

    // Mark a payment as finalized.
    bool MarkFinalized(long chainId, string paymentReference, DateTimeOffset finalizedAt);

    // Query payment(s) by reference.
    PaymentRecord? GetPayment(long chainId, string paymentReference);

    IEnumerable<PaymentTransferRecord> GetTransfersByReference(long chainId, string paymentReference);
}

// Per-log transfer row in payment_transfers
public sealed record PaymentTransferRecord(
    long ChainId,
    string TxHash,
    int LogIndex,
    int? TransactionIndex,
    long? BlockNumber,
    string PaymentReference,
    string GatewayAddress,
    string? PayerAddress,
    BigInteger? AmountWei,
    DateTimeOffset CreatedAt
);

// Aggregated payment row in payments (one per payment_reference)
public sealed record PaymentRecord(
    long ChainId,
    string PaymentReference,
    string GatewayAddress,
    string? PayerAddress,
    BigInteger? TotalAmountWei,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset? FinalizedAt,
    long? FirstBlockNumber,
    string? FirstTxHash,
    int? FirstLogIndex,
    long? LastBlockNumber,
    string? LastTxHash,
    int? LastLogIndex
);

public sealed class PostgresPaymentStore : IPaymentStore
{
    private readonly string _connString;
    private readonly ILogger<PostgresPaymentStore> _logger;

    public PostgresPaymentStore(string connString, ILogger<PostgresPaymentStore> logger)
    {
        _connString = connString;
        _logger = logger;
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        _logger.LogInformation("EnsureSchema starting for payments...");
        try
        {
            using var conn = new NpgsqlConnection(_connString);
            conn.Open();

            // Create payment_transfers table (per-log)
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS payment_transfers (
  chain_id           bigint       NOT NULL,
  tx_hash            text         NOT NULL,
  log_index          integer      NOT NULL,
  transaction_index  integer      NULL,
  block_number       bigint       NULL,

  payment_reference  text         NOT NULL,
  gateway_address    text         NOT NULL,
  payer_address      text         NULL,
  amount_wei         numeric(78,0) NULL,

  created_at         timestamptz  NOT NULL,

  CONSTRAINT pk_payment_transfers PRIMARY KEY (chain_id, tx_hash, log_index)
);";
                cmd.ExecuteNonQuery();
            }

            using (var idx = conn.CreateCommand())
            {
                idx.CommandText = "CREATE INDEX IF NOT EXISTS ix_payment_transfers_ref ON payment_transfers (payment_reference);";
                idx.ExecuteNonQuery();
            }
            using (var idxb = conn.CreateCommand())
            {
                idxb.CommandText = "CREATE INDEX IF NOT EXISTS ix_payment_transfers_block ON payment_transfers (block_number);";
                idxb.ExecuteNonQuery();
            }
            using (var idxg = conn.CreateCommand())
            {
                idxg.CommandText = "CREATE INDEX IF NOT EXISTS ix_payment_transfers_gateway ON payment_transfers (gateway_address);";
                idxg.ExecuteNonQuery();
            }

            // Create payments table (aggregated per reference)
            using (var cmd2 = conn.CreateCommand())
            {
                cmd2.CommandText = @"
CREATE TABLE IF NOT EXISTS payments (
  chain_id           bigint       NOT NULL,
  payment_reference  text         NOT NULL,

  gateway_address    text         NOT NULL,
  payer_address      text         NULL,

  total_amount_wei   numeric(78,0) NULL,

  status             text         NOT NULL DEFAULT 'observed',
  created_at         timestamptz  NOT NULL,
  confirmed_at       timestamptz  NULL,
  finalized_at       timestamptz  NULL,

  first_block_number bigint       NULL,
  first_tx_hash      text         NULL,
  first_log_index    integer      NULL,

  last_block_number  bigint       NULL,
  last_tx_hash       text         NULL,
  last_log_index     integer      NULL,

  CONSTRAINT pk_payments PRIMARY KEY (chain_id, payment_reference)
);";
                cmd2.ExecuteNonQuery();
            }

            using (var ipg = conn.CreateCommand())
            {
                ipg.CommandText = "CREATE INDEX IF NOT EXISTS ix_payments_gateway ON payments (gateway_address);";
                ipg.ExecuteNonQuery();
            }
            using (var ipp = conn.CreateCommand())
            {
                ipp.CommandText = "CREATE INDEX IF NOT EXISTS ix_payments_payer ON payments (payer_address);";
                ipp.ExecuteNonQuery();
            }
            using (var ipb = conn.CreateCommand())
            {
                ipb.CommandText = "CREATE INDEX IF NOT EXISTS ix_payments_block ON payments (first_block_number);";
                ipb.ExecuteNonQuery();
            }
            _logger.LogInformation("EnsureSchema completed for payments.");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "FATAL: Failed to ensure Postgres schema for payments/payment_transfers");
            throw;
        }
    }

    public void UpsertObservedTransfer(PaymentTransferRecord t)
    {
        using var conn = new NpgsqlConnection(_connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO payment_transfers (
  chain_id, tx_hash, log_index, transaction_index, block_number, payment_reference,
  gateway_address, payer_address, amount_wei, created_at)
VALUES (@chain, @tx, @log, @tix, @block, @ref, @gw, @payer, CAST(@amt AS numeric), @created)
ON CONFLICT (chain_id, tx_hash, log_index)
DO UPDATE SET
  transaction_index = COALESCE(EXCLUDED.transaction_index, payment_transfers.transaction_index),
  block_number      = COALESCE(EXCLUDED.block_number, payment_transfers.block_number),
  payer_address     = COALESCE(EXCLUDED.payer_address, payment_transfers.payer_address),
  amount_wei        = COALESCE(EXCLUDED.amount_wei, payment_transfers.amount_wei),
  gateway_address   = EXCLUDED.gateway_address,
  payment_reference = EXCLUDED.payment_reference,
  created_at        = LEAST(payment_transfers.created_at, EXCLUDED.created_at);";
        cmd.Parameters.AddWithValue("@chain", t.ChainId);
        cmd.Parameters.AddWithValue("@tx", t.TxHash.ToLowerInvariant());
        cmd.Parameters.AddWithValue("@log", t.LogIndex);
        cmd.Parameters.AddWithValue("@tix", (object?)t.TransactionIndex ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@block", (object?)t.BlockNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ref", t.PaymentReference);
        cmd.Parameters.AddWithValue("@gw", t.GatewayAddress.ToLowerInvariant());
        cmd.Parameters.AddWithValue("@payer", (object?)t.PayerAddress?.ToLowerInvariant() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@amt", (object?)t.AmountWei?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@created", t.CreatedAt);
        cmd.ExecuteNonQuery();
    }

    public PaymentRecord? UpsertAndGetPayment(long chainId, string paymentReference)
    {
        using var conn = new NpgsqlConnection(_connString);
        conn.Open();

        // Aggregate from payment_transfers
        using (var agg = conn.CreateCommand())
        {
            agg.CommandText = @"
WITH s AS (
  SELECT 
    chain_id,
    payment_reference,
    MIN(gateway_address) AS gateway_address,
    MIN(payer_address)   AS payer_address,
    SUM(amount_wei)      AS total_amount_wei,
    MIN(created_at)      AS created_at,
    MIN(block_number)    AS first_block_number,
    (ARRAY_AGG(tx_hash ORDER BY block_number, transaction_index, log_index))[1]  AS first_tx_hash,
    (ARRAY_AGG(log_index ORDER BY block_number, transaction_index, log_index))[1] AS first_log_index,
    MAX(block_number)    AS last_block_number,
    (ARRAY_AGG(tx_hash ORDER BY block_number DESC, transaction_index DESC, log_index DESC))[1]  AS last_tx_hash,
    (ARRAY_AGG(log_index ORDER BY block_number DESC, transaction_index DESC, log_index DESC))[1] AS last_log_index
  FROM payment_transfers
  WHERE chain_id=@c AND payment_reference=@r
  GROUP BY chain_id, payment_reference
)
INSERT INTO payments (
  chain_id, payment_reference, gateway_address, payer_address, total_amount_wei, status, created_at,
  confirmed_at, finalized_at,
  first_block_number, first_tx_hash, first_log_index,
  last_block_number, last_tx_hash, last_log_index)
SELECT 
  s.chain_id, s.payment_reference, s.gateway_address, s.payer_address, s.total_amount_wei, 'observed', s.created_at,
  NULL, NULL,
  s.first_block_number, s.first_tx_hash, s.first_log_index,
  s.last_block_number, s.last_tx_hash, s.last_log_index
FROM s
ON CONFLICT (chain_id, payment_reference)
DO UPDATE SET
  gateway_address    = EXCLUDED.gateway_address,
  payer_address      = EXCLUDED.payer_address,
  total_amount_wei   = EXCLUDED.total_amount_wei,
  created_at         = LEAST(payments.created_at, EXCLUDED.created_at),
  first_block_number = COALESCE(payments.first_block_number, EXCLUDED.first_block_number),
  first_tx_hash      = COALESCE(payments.first_tx_hash, EXCLUDED.first_tx_hash),
  first_log_index    = COALESCE(payments.first_log_index, EXCLUDED.first_log_index),
  last_block_number  = GREATEST(payments.last_block_number, EXCLUDED.last_block_number),
  last_tx_hash       = COALESCE(EXCLUDED.last_tx_hash, payments.last_tx_hash),
  last_log_index     = COALESCE(EXCLUDED.last_log_index, payments.last_log_index)
RETURNING chain_id, payment_reference, gateway_address, payer_address, total_amount_wei::text, status, created_at,
          confirmed_at, finalized_at, first_block_number, first_tx_hash, first_log_index, last_block_number, last_tx_hash, last_log_index;";
            agg.Parameters.AddWithValue("@c", chainId);
            agg.Parameters.AddWithValue("@r", paymentReference);
            using var reader = agg.ExecuteReader();
            if (!reader.Read()) return null;
            BigInteger? totalWei = null;
            if (!reader.IsDBNull(4))
            {
                var txt = reader.GetString(4);
                if (BigInteger.TryParse(txt, out var bi)) totalWei = bi;
            }
            return new PaymentRecord(
                ChainId: reader.GetInt64(0),
                PaymentReference: reader.GetString(1),
                GatewayAddress: reader.GetString(2),
                PayerAddress: reader.IsDBNull(3) ? null : reader.GetString(3),
                TotalAmountWei: totalWei,
                Status: reader.GetString(5),
                CreatedAt: reader.GetFieldValue<DateTimeOffset>(6),
                ConfirmedAt: reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
                FinalizedAt: reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
                FirstBlockNumber: reader.IsDBNull(9) ? null : reader.GetInt64(9),
                FirstTxHash: reader.IsDBNull(10) ? null : reader.GetString(10),
                FirstLogIndex: reader.IsDBNull(11) ? null : reader.GetInt32(11),
                LastBlockNumber: reader.IsDBNull(12) ? null : reader.GetInt64(12),
                LastTxHash: reader.IsDBNull(13) ? null : reader.GetString(13),
                LastLogIndex: reader.IsDBNull(14) ? null : reader.GetInt32(14)
            );
        }
    }

    public bool MarkConfirmed(long chainId, string paymentReference, long blockNumber, DateTimeOffset confirmedAt)
    {
        using var conn = new NpgsqlConnection(_connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE payments SET status='confirmed', confirmed_at=@t, first_block_number=COALESCE(first_block_number, @b)
WHERE chain_id=@c AND payment_reference=@r AND status <> 'finalized'";
        cmd.Parameters.AddWithValue("@t", confirmedAt);
        cmd.Parameters.AddWithValue("@b", blockNumber);
        cmd.Parameters.AddWithValue("@c", chainId);
        cmd.Parameters.AddWithValue("@r", paymentReference);
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool MarkFinalized(long chainId, string paymentReference, DateTimeOffset finalizedAt)
    {
        using var conn = new NpgsqlConnection(_connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE payments SET status='finalized', finalized_at=@t
WHERE chain_id=@c AND payment_reference=@r";
        cmd.Parameters.AddWithValue("@t", finalizedAt);
        cmd.Parameters.AddWithValue("@c", chainId);
        cmd.Parameters.AddWithValue("@r", paymentReference);
        return cmd.ExecuteNonQuery() > 0;
    }

    public PaymentRecord? GetPayment(long chainId, string paymentReference)
    {
        using var conn = new NpgsqlConnection(_connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT chain_id, payment_reference, gateway_address, payer_address, total_amount_wei::text, status, created_at,
       confirmed_at, finalized_at, first_block_number, first_tx_hash, first_log_index, last_block_number, last_tx_hash, last_log_index
FROM payments WHERE chain_id=@c AND payment_reference=@r";
        cmd.Parameters.AddWithValue("@c", chainId);
        cmd.Parameters.AddWithValue("@r", paymentReference);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        BigInteger? totalWei = null;
        if (!reader.IsDBNull(4))
        {
            var txt = reader.GetString(4);
            if (BigInteger.TryParse(txt, out var bi)) totalWei = bi;
        }
        return new PaymentRecord(
            ChainId: reader.GetInt64(0),
            PaymentReference: reader.GetString(1),
            GatewayAddress: reader.GetString(2),
            PayerAddress: reader.IsDBNull(3) ? null : reader.GetString(3),
            TotalAmountWei: totalWei,
            Status: reader.GetString(5),
            CreatedAt: reader.GetFieldValue<DateTimeOffset>(6),
            ConfirmedAt: reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
            FinalizedAt: reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
            FirstBlockNumber: reader.IsDBNull(9) ? null : reader.GetInt64(9),
            FirstTxHash: reader.IsDBNull(10) ? null : reader.GetString(10),
            FirstLogIndex: reader.IsDBNull(11) ? null : reader.GetInt32(11),
            LastBlockNumber: reader.IsDBNull(12) ? null : reader.GetInt64(12),
            LastTxHash: reader.IsDBNull(13) ? null : reader.GetString(13),
            LastLogIndex: reader.IsDBNull(14) ? null : reader.GetInt32(14)
        );
    }

    public IEnumerable<PaymentTransferRecord> GetTransfersByReference(long chainId, string paymentReference)
    {
        using var conn = new NpgsqlConnection(_connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT chain_id, tx_hash, log_index, transaction_index, block_number,
       payment_reference, gateway_address, payer_address, amount_wei::text, created_at
FROM payment_transfers WHERE chain_id=@c AND payment_reference=@r ORDER BY block_number, transaction_index, log_index";
        cmd.Parameters.AddWithValue("@c", chainId);
        cmd.Parameters.AddWithValue("@r", paymentReference);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            BigInteger? amount = null;
            if (!reader.IsDBNull(8))
            {
                var txt = reader.GetString(8);
                if (BigInteger.TryParse(txt, out var bi)) amount = bi;
            }
            yield return new PaymentTransferRecord(
                ChainId: reader.GetInt64(0),
                TxHash: reader.GetString(1),
                LogIndex: reader.GetInt32(2),
                TransactionIndex: reader.IsDBNull(3) ? null : reader.GetInt32(3),
                BlockNumber: reader.IsDBNull(4) ? null : reader.GetInt64(4),
                PaymentReference: reader.GetString(5),
                GatewayAddress: reader.GetString(6),
                PayerAddress: reader.IsDBNull(7) ? null : reader.GetString(7),
                AmountWei: amount,
                CreatedAt: reader.GetFieldValue<DateTimeOffset>(9)
            );
        }
    }
}
