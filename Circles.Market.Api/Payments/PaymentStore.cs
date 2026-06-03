using Npgsql;
using System.Numerics;

namespace Circles.Market.Api.Payments;

public interface IPaymentStore
{
    // Upsert a single on-chain transfer (log-level).
    void UpsertObservedTransfer(PaymentTransferRecord transfer);

    // Recompute and persist the aggregated payment row for a payment_reference. Return row or null.
    // Only transfers flagged eligible (token trusted by the receiving gateway) contribute to
    // total_amount_wei; untrusted and undetermined transfers are recorded but never credited.
    PaymentRecord? UpsertAndGetPayment(long chainId, string paymentReference);

    // Mark a payment as confirmed.
    bool MarkConfirmed(long chainId, string paymentReference, long blockNumber, DateTimeOffset confirmedAt);

    // Mark a payment as finalized.
    bool MarkFinalized(long chainId, string paymentReference, DateTimeOffset finalizedAt);

    // Query payment(s) by reference.
    PaymentRecord? GetPayment(long chainId, string paymentReference);

    IEnumerable<PaymentTransferRecord> GetTransfersByReference(long chainId, string paymentReference);

    // Transfers whose token-trust eligibility has not yet been resolved (eligible IS NULL) but which
    // carry a token avatar we can decide on. Used by the poller's re-evaluation pass.
    IEnumerable<PaymentTransferRecord> GetUndeterminedTransfers(long chainId, int limit);

    // Resolve a previously-undetermined transfer's eligibility. No-op once already determined.
    void SetTransferEligibility(long chainId, string txHash, int logIndex, bool eligible);
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
    DateTimeOffset CreatedAt,
    // ERC1155 token id of the received CRC (CrcV2_PaymentGateway.PaymentReceived.tokenId).
    BigInteger? TokenId = null,
    // Avatar address derived from TokenId (low 160 bits, 0x + 40 hex lowercased).
    string? TokenAvatar = null,
    // Token-trust eligibility: true = gateway trusts the token, false = does not, null = undetermined.
    bool? Eligible = null
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

            // Additive migration: token-trust columns. Existing rows get NULLs (undetermined) and are
            // re-evaluated by the poller. token_id/token_avatar record which CRC was received; eligible
            // captures whether the receiving gateway trusts that token.
            using (var alt = conn.CreateCommand())
            {
                alt.CommandText = @"
ALTER TABLE payment_transfers ADD COLUMN IF NOT EXISTS token_id      numeric(78,0) NULL;
ALTER TABLE payment_transfers ADD COLUMN IF NOT EXISTS token_avatar  text          NULL;
ALTER TABLE payment_transfers ADD COLUMN IF NOT EXISTS eligible      boolean       NULL;";
                alt.ExecuteNonQuery();
            }

            // Partial index to keep the re-evaluation scan cheap (only undetermined-but-decidable rows).
            using (var idxe = conn.CreateCommand())
            {
                idxe.CommandText = "CREATE INDEX IF NOT EXISTS ix_payment_transfers_undetermined ON payment_transfers (chain_id, block_number) WHERE eligible IS NULL AND token_avatar IS NOT NULL;";
                idxe.ExecuteNonQuery();
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

        // Best-effort hardening (non-fatal): immunize identifier columns against glibc collation drift.
        EnsureIdentifierCollationsToC();
    }

    // Pin ASCII identifier columns to COLLATE "C" (raw byte ordering, encoding/locale independent).
    // Rationale: a glibc collation change silently corrupted pk_payments in prod — byte-identical
    // (chain_id, payment_reference) keys slipped past the uniqueness check, producing duplicate rows
    // under a valid unique index and stranding fully-paid orders. "C" cannot drift across libc
    // versions, so payment_reference / tx_hash / gateway / payer / token_avatar become permanently
    // immune. Self-discovering + idempotent: only text columns not already "C" are selected, so it
    // applies once then no-ops on every subsequent start. Best-effort: it takes a brief ACCESS
    // EXCLUSIVE lock to rebuild dependent indexes, bounded by lock_timeout, and never fails the boot.
    private void EnsureIdentifierCollationsToC()
    {
        const string sql = @"
DO $$ DECLARE r record; BEGIN
  PERFORM set_config('lock_timeout', '3s', true);
  FOR r IN
    SELECT c.relname AS tbl, a.attname AS col
    FROM pg_attribute a
    JOIN pg_class c ON c.oid = a.attrelid
    JOIN pg_namespace n ON n.oid = c.relnamespace
    JOIN pg_collation co ON co.oid = a.attcollation
    WHERE n.nspname = 'public'
      AND c.relname = ANY (ARRAY['payments','payment_transfers'])
      AND a.attnum > 0 AND NOT a.attisdropped
      AND a.atttypid = 'text'::regtype
      AND co.collname <> 'C'
  LOOP
    EXECUTE format('ALTER TABLE public.%I ALTER COLUMN %I TYPE text COLLATE ""C""', r.tbl, r.col);
    RAISE NOTICE 'collation->C: %.%', r.tbl, r.col;
  END LOOP;
END $$;";
        try
        {
            using var conn = new NpgsqlConnection(_connString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
            _logger.LogInformation("Identifier collation (COLLATE \"C\") ensured for payment tables.");
        }
        catch (Exception ex)
        {
            // Non-fatal, but make it observable: a duplicate-key error here means live duplicate
            // rows under a unique index (drift already struck) — alert on this metric, don't ignore.
            Circles.Market.Api.Metrics.MarketplaceMetrics.SchemaCollationMigrationFailures.WithLabels("payments").Inc();
            _logger.LogWarning(ex, "Non-fatal: could not ensure COLLATE \"C\" on payment identifier columns; will retry next start.");
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
  gateway_address, payer_address, amount_wei, created_at,
  token_id, token_avatar, eligible)
VALUES (@chain, @tx, @log, @tix, @block, @ref, @gw, @payer, CAST(@amt AS numeric), @created,
        CAST(@tokenId AS numeric), @tokenAvatar, @eligible)
ON CONFLICT (chain_id, tx_hash, log_index)
DO UPDATE SET
  transaction_index = COALESCE(EXCLUDED.transaction_index, payment_transfers.transaction_index),
  block_number      = COALESCE(EXCLUDED.block_number, payment_transfers.block_number),
  payer_address     = COALESCE(EXCLUDED.payer_address, payment_transfers.payer_address),
  amount_wei        = COALESCE(EXCLUDED.amount_wei, payment_transfers.amount_wei),
  gateway_address   = EXCLUDED.gateway_address,
  payment_reference = EXCLUDED.payment_reference,
  created_at        = LEAST(payment_transfers.created_at, EXCLUDED.created_at),
  token_id          = COALESCE(payment_transfers.token_id, EXCLUDED.token_id),
  token_avatar      = COALESCE(payment_transfers.token_avatar, EXCLUDED.token_avatar),
  -- Eligibility is sticky once determined: keep an existing true/false, only fill a NULL.
  eligible          = COALESCE(payment_transfers.eligible, EXCLUDED.eligible);";
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
        cmd.Parameters.AddWithValue("@tokenId", (object?)t.TokenId?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tokenAvatar", (object?)t.TokenAvatar?.ToLowerInvariant() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@eligible", (object?)t.Eligible ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public PaymentRecord? UpsertAndGetPayment(long chainId, string paymentReference)
    {
        using var conn = new NpgsqlConnection(_connString);
        conn.Open();

        // Only transfers in a token the receiving gateway trusts contribute to the paid total.
        // Untrusted (eligible=false) and undetermined (eligible IS NULL) transfers are recorded but
        // never credited, so an order is only marked paid by trusted-token value.
        const string sumExpr = "SUM(amount_wei) FILTER (WHERE eligible IS TRUE)";

        // Aggregate from payment_transfers
        using (var agg = conn.CreateCommand())
        {
            agg.CommandText = $@"
WITH s AS (
  SELECT
    chain_id,
    payment_reference,
    MIN(gateway_address) AS gateway_address,
    MIN(payer_address)   AS payer_address,
    {sumExpr}      AS total_amount_wei,
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
       payment_reference, gateway_address, payer_address, amount_wei::text, created_at,
       token_id::text, token_avatar, eligible
FROM payment_transfers WHERE chain_id=@c AND payment_reference=@r ORDER BY block_number, transaction_index, log_index";
        cmd.Parameters.AddWithValue("@c", chainId);
        cmd.Parameters.AddWithValue("@r", paymentReference);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            yield return MapTransfer(reader);
        }
    }

    public IEnumerable<PaymentTransferRecord> GetUndeterminedTransfers(long chainId, int limit)
    {
        using var conn = new NpgsqlConnection(_connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT chain_id, tx_hash, log_index, transaction_index, block_number,
       payment_reference, gateway_address, payer_address, amount_wei::text, created_at,
       token_id::text, token_avatar, eligible
FROM payment_transfers
WHERE chain_id=@c AND eligible IS NULL AND token_avatar IS NOT NULL
ORDER BY block_number ASC NULLS LAST
LIMIT @lim";
        cmd.Parameters.AddWithValue("@c", chainId);
        cmd.Parameters.AddWithValue("@lim", Math.Max(1, limit));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            yield return MapTransfer(reader);
        }
    }

    public void SetTransferEligibility(long chainId, string txHash, int logIndex, bool eligible)
    {
        using var conn = new NpgsqlConnection(_connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        // Only resolve undetermined rows so an already-decided value never flips.
        cmd.CommandText = @"
UPDATE payment_transfers SET eligible=@e
WHERE chain_id=@c AND tx_hash=@tx AND log_index=@log AND eligible IS NULL";
        cmd.Parameters.AddWithValue("@e", eligible);
        cmd.Parameters.AddWithValue("@c", chainId);
        cmd.Parameters.AddWithValue("@tx", txHash.ToLowerInvariant());
        cmd.Parameters.AddWithValue("@log", logIndex);
        cmd.ExecuteNonQuery();
    }

    // Maps a payment_transfers row in the column order used by the SELECTs above.
    private static PaymentTransferRecord MapTransfer(NpgsqlDataReader reader)
    {
        BigInteger? amount = null;
        if (!reader.IsDBNull(8) && BigInteger.TryParse(reader.GetString(8), out var amt)) amount = amt;

        BigInteger? tokenId = null;
        if (!reader.IsDBNull(10) && BigInteger.TryParse(reader.GetString(10), out var tid)) tokenId = tid;

        return new PaymentTransferRecord(
            ChainId: reader.GetInt64(0),
            TxHash: reader.GetString(1),
            LogIndex: reader.GetInt32(2),
            TransactionIndex: reader.IsDBNull(3) ? null : reader.GetInt32(3),
            BlockNumber: reader.IsDBNull(4) ? null : reader.GetInt64(4),
            PaymentReference: reader.GetString(5),
            GatewayAddress: reader.GetString(6),
            PayerAddress: reader.IsDBNull(7) ? null : reader.GetString(7),
            AmountWei: amount,
            CreatedAt: reader.GetFieldValue<DateTimeOffset>(9),
            TokenId: tokenId,
            TokenAvatar: reader.IsDBNull(11) ? null : reader.GetString(11),
            Eligible: reader.IsDBNull(12) ? null : reader.GetBoolean(12)
        );
    }
}
