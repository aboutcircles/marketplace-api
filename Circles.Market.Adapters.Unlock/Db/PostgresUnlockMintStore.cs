using System.Text.Json;
using Npgsql;

namespace Circles.Market.Adapters.Unlock.Db;

public interface IUnlockMintStore
{
    Task<UnlockMintRecord?> GetByPaymentReferenceAsync(long chainId, string sellerAddress, string paymentReference, CancellationToken ct);
    Task<IReadOnlyList<UnlockMintTicketRecord>> ListTicketsByPaymentReferenceAsync(long chainId, string sellerAddress, string paymentReference, CancellationToken ct);
    Task UpsertMintAsync(UnlockMintRecord record, CancellationToken ct);
    Task UpsertMintTicketsAsync(IReadOnlyList<UnlockMintTicketRecord> tickets, CancellationToken ct);
    Task UpsertMintWithTicketsAsync(UnlockMintRecord record, IReadOnlyList<UnlockMintTicketRecord> tickets, CancellationToken ct);
    Task<long> CountSoldAsync(long chainId, string sellerAddress, string sku, CancellationToken ct);
}

public sealed class PostgresUnlockMintStore : IUnlockMintStore
{
    private readonly string _connString;

    public PostgresUnlockMintStore(string connString)
    {
        _connString = connString ?? throw new ArgumentNullException(nameof(connString));
    }

    public async Task<UnlockMintRecord?> GetByPaymentReferenceAsync(long chainId, string sellerAddress, string paymentReference, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT chain_id, seller_address, payment_reference, order_id, sku, buyer_address, lock_address,
       quantity, transaction_hash, key_id, expiration_unix, status, warning, error,
       CASE WHEN response_json IS NULL THEN NULL ELSE response_json::text END
FROM unlock_mints
WHERE chain_id=$1 AND seller_address=$2 AND payment_reference=$3
LIMIT 1";
        cmd.Parameters.AddWithValue(chainId);
        cmd.Parameters.AddWithValue(sellerAddress.Trim().ToLowerInvariant());
        cmd.Parameters.AddWithValue(paymentReference.Trim());

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new UnlockMintRecord
        {
            ChainId = reader.GetInt64(0),
            SellerAddress = reader.GetString(1),
            PaymentReference = reader.GetString(2),
            OrderId = reader.GetString(3),
            Sku = reader.GetString(4),
            BuyerAddress = reader.GetString(5),
            LockAddress = reader.GetString(6),
            Quantity = reader.GetInt64(7),
            TransactionHash = reader.IsDBNull(8) ? null : reader.GetString(8),
            KeyId = reader.IsDBNull(9) ? null : reader.GetString(9),
            ExpirationUnix = reader.IsDBNull(10) ? null : reader.GetInt64(10),
            Status = reader.GetString(11),
            Warning = reader.IsDBNull(12) ? null : reader.GetString(12),
            Error = reader.IsDBNull(13) ? null : reader.GetString(13),
            ResponseJson = reader.IsDBNull(14) ? null : reader.GetString(14)
        };
    }

    public async Task<IReadOnlyList<UnlockMintTicketRecord>> ListTicketsByPaymentReferenceAsync(
        long chainId,
        string sellerAddress,
        string paymentReference,
        CancellationToken ct)
    {
        var result = new List<UnlockMintTicketRecord>();

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT chain_id, seller_address, payment_reference, ticket_index, order_id, sku, buyer_address,
       lock_address, transaction_hash, key_id, expiration_unix, status, warning, error,
       CASE WHEN ticket_json IS NULL THEN NULL ELSE ticket_json::text END,
       qrcode_data_url
FROM unlock_mint_tickets
WHERE chain_id=$1 AND seller_address=$2 AND payment_reference=$3
ORDER BY ticket_index";
        cmd.Parameters.AddWithValue(chainId);
        cmd.Parameters.AddWithValue(sellerAddress.Trim().ToLowerInvariant());
        cmd.Parameters.AddWithValue(paymentReference.Trim());

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new UnlockMintTicketRecord
            {
                ChainId = reader.GetInt64(0),
                SellerAddress = reader.GetString(1),
                PaymentReference = reader.GetString(2),
                TicketIndex = reader.GetInt32(3),
                OrderId = reader.GetString(4),
                Sku = reader.GetString(5),
                BuyerAddress = reader.GetString(6),
                LockAddress = reader.GetString(7),
                TransactionHash = reader.IsDBNull(8) ? null : reader.GetString(8),
                KeyId = reader.IsDBNull(9) ? null : reader.GetString(9),
                ExpirationUnix = reader.IsDBNull(10) ? null : reader.GetInt64(10),
                Status = reader.GetString(11),
                Warning = reader.IsDBNull(12) ? null : reader.GetString(12),
                Error = reader.IsDBNull(13) ? null : reader.GetString(13),
                TicketJson = reader.IsDBNull(14) ? null : reader.GetString(14),
                QrcodeDataUrl = reader.IsDBNull(15) ? null : reader.GetString(15)
            });
        }

        return result;
    }

    public async Task UpsertMintAsync(UnlockMintRecord record, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);
        await UpsertMintInternalAsync(conn, record, ct);
    }

    public async Task UpsertMintWithTicketsAsync(UnlockMintRecord record, IReadOnlyList<UnlockMintTicketRecord> tickets, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await UpsertMintInternalAsync(conn, record, ct, tx);
        await UpsertMintTicketsInternalAsync(conn, tickets, ct, tx);

        await tx.CommitAsync(ct);
    }

    private static async Task UpsertMintInternalAsync(NpgsqlConnection conn, UnlockMintRecord record, CancellationToken ct, NpgsqlTransaction? tx = null)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
INSERT INTO unlock_mints(
  chain_id, seller_address, payment_reference, order_id, sku, buyer_address, lock_address,
  quantity,
  transaction_hash, key_id, expiration_unix, status, warning, error, response_json,
  created_at, updated_at
)
VALUES (
  $1, $2, $3, $4, $5, $6, $7,
  $8,
  $9, $10, $11, $12, $13, $14, $15::jsonb,
  now(), now()
)
ON CONFLICT (chain_id, seller_address, payment_reference) DO UPDATE SET
  order_id = EXCLUDED.order_id,
  sku = EXCLUDED.sku,
  buyer_address = EXCLUDED.buyer_address,
  lock_address = EXCLUDED.lock_address,
  quantity = EXCLUDED.quantity,
  transaction_hash = EXCLUDED.transaction_hash,
  key_id = EXCLUDED.key_id,
  expiration_unix = EXCLUDED.expiration_unix,
  status = EXCLUDED.status,
  warning = EXCLUDED.warning,
  error = EXCLUDED.error,
  response_json = EXCLUDED.response_json,
  updated_at = now()";

        cmd.Parameters.AddWithValue(record.ChainId);
        cmd.Parameters.AddWithValue(record.SellerAddress.Trim().ToLowerInvariant());
        cmd.Parameters.AddWithValue(record.PaymentReference.Trim());
        cmd.Parameters.AddWithValue(record.OrderId.Trim());
        cmd.Parameters.AddWithValue(record.Sku.Trim().ToLowerInvariant());
        cmd.Parameters.AddWithValue(record.BuyerAddress.Trim().ToLowerInvariant());
        cmd.Parameters.AddWithValue(record.LockAddress.Trim().ToLowerInvariant());
        cmd.Parameters.AddWithValue(record.Quantity);
        cmd.Parameters.AddWithValue((object?)record.TransactionHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)record.KeyId ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)record.ExpirationUnix ?? DBNull.Value);
        cmd.Parameters.AddWithValue(record.Status.Trim().ToLowerInvariant());
        cmd.Parameters.AddWithValue((object?)record.Warning ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)record.Error ?? DBNull.Value);

        if (string.IsNullOrWhiteSpace(record.ResponseJson))
        {
            cmd.Parameters.AddWithValue((object)DBNull.Value);
        }
        else
        {
            using var doc = JsonDocument.Parse(record.ResponseJson);
            cmd.Parameters.AddWithValue(doc.RootElement.GetRawText());
        }

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertMintTicketsAsync(IReadOnlyList<UnlockMintTicketRecord> tickets, CancellationToken ct)
    {
        if (tickets.Count == 0)
        {
            return;
        }

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await UpsertMintTicketsInternalAsync(conn, tickets, ct, tx);

        await tx.CommitAsync(ct);
    }

    private static async Task UpsertMintTicketsInternalAsync(NpgsqlConnection conn, IReadOnlyList<UnlockMintTicketRecord> tickets, CancellationToken ct, NpgsqlTransaction? tx = null)
    {
        if (tickets.Count == 0)
        {
            return;
        }

        foreach (var ticket in tickets)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT INTO unlock_mint_tickets(
  chain_id, seller_address, payment_reference, ticket_index, order_id, sku, buyer_address,
  lock_address, transaction_hash, key_id, expiration_unix, status, warning, error, ticket_json, qrcode_data_url,
  created_at, updated_at
)
VALUES (
  $1, $2, $3, $4, $5, $6, $7,
  $8, $9, $10, $11, $12, $13, $14, $15::jsonb, $16,
  now(), now()
)
ON CONFLICT (chain_id, seller_address, payment_reference, ticket_index) DO UPDATE SET
  order_id = EXCLUDED.order_id,
  sku = EXCLUDED.sku,
  buyer_address = EXCLUDED.buyer_address,
  lock_address = EXCLUDED.lock_address,
  transaction_hash = EXCLUDED.transaction_hash,
  key_id = EXCLUDED.key_id,
  expiration_unix = EXCLUDED.expiration_unix,
  status = EXCLUDED.status,
  warning = EXCLUDED.warning,
  error = EXCLUDED.error,
  ticket_json = EXCLUDED.ticket_json,
  qrcode_data_url = EXCLUDED.qrcode_data_url,
  updated_at = now()";

            cmd.Parameters.AddWithValue(ticket.ChainId);
            cmd.Parameters.AddWithValue(ticket.SellerAddress.Trim().ToLowerInvariant());
            cmd.Parameters.AddWithValue(ticket.PaymentReference.Trim());
            cmd.Parameters.AddWithValue(ticket.TicketIndex);
            cmd.Parameters.AddWithValue(ticket.OrderId.Trim());
            cmd.Parameters.AddWithValue(ticket.Sku.Trim().ToLowerInvariant());
            cmd.Parameters.AddWithValue(ticket.BuyerAddress.Trim().ToLowerInvariant());
            cmd.Parameters.AddWithValue(ticket.LockAddress.Trim().ToLowerInvariant());
            cmd.Parameters.AddWithValue((object?)ticket.TransactionHash ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)ticket.KeyId ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)ticket.ExpirationUnix ?? DBNull.Value);
            cmd.Parameters.AddWithValue(ticket.Status.Trim().ToLowerInvariant());
            cmd.Parameters.AddWithValue((object?)ticket.Warning ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)ticket.Error ?? DBNull.Value);

            if (string.IsNullOrWhiteSpace(ticket.TicketJson))
            {
                cmd.Parameters.AddWithValue((object)DBNull.Value);
            }
            else
            {
                using var doc = JsonDocument.Parse(ticket.TicketJson);
                cmd.Parameters.AddWithValue(doc.RootElement.GetRawText());
            }

            cmd.Parameters.AddWithValue((object?)ticket.QrcodeDataUrl ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<long> CountSoldAsync(long chainId, string sellerAddress, string sku, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT COALESCE(SUM(quantity), 0)
FROM unlock_mints
WHERE chain_id=$1 AND seller_address=$2 AND sku=$3 AND status='ok'";
        cmd.Parameters.AddWithValue(chainId);
        cmd.Parameters.AddWithValue(sellerAddress.Trim().ToLowerInvariant());
        cmd.Parameters.AddWithValue(sku.Trim().ToLowerInvariant());

        object? scalar = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(scalar ?? 0L);
    }
}
