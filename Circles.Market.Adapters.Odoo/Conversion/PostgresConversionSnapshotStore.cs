using Npgsql;

namespace Circles.Market.Adapters.Odoo.Conversion;

public interface IConversionSnapshotStore
{
    Task EnsureSchemaAsync(CancellationToken ct = default);
    Task StoreAsync(ConversionSnapshot snapshot, CancellationToken ct = default);
    Task<ConversionSnapshot?> GetByOrderIdAsync(string orderId, CancellationToken ct = default);
}

public sealed class PostgresConversionSnapshotStore : IConversionSnapshotStore
{
    private readonly string _connString;
    private readonly ILogger<PostgresConversionSnapshotStore> _log;

    public PostgresConversionSnapshotStore(string connString, ILogger<PostgresConversionSnapshotStore> log)
    {
        _connString = connString;
        _log = log;
    }

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS conversion_snapshots (
  order_id           text PRIMARY KEY,
  payment_reference  text NOT NULL,
  payment_timestamp  text NULL,
  amount_wei         text NULL,
  amount_crc         numeric(38,18) NULL,
  scrc_xdai_rate     numeric(38,18) NULL,
  conversion_factor  numeric(38,18) NULL,
  dcrc_xdai_rate     numeric(38,18) NULL,
  xdai_eur_rate      numeric(38,18) NULL,
  eur_equivalent     numeric(38,18) NULL,
  price_date         text NULL,
  pricing_source     text NULL,
  generated_at       timestamptz NOT NULL,
  created_at         timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_conversion_snapshots_payref
  ON conversion_snapshots (payment_reference);
";
        await cmd.ExecuteNonQueryAsync(ct);
        _log.LogInformation("Ensured conversion_snapshots schema");
    }

    public async Task StoreAsync(ConversionSnapshot snapshot, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO conversion_snapshots (
    order_id, payment_reference, payment_timestamp,
    amount_wei, amount_crc, scrc_xdai_rate, conversion_factor,
    dcrc_xdai_rate, xdai_eur_rate, eur_equivalent,
    price_date, pricing_source, generated_at
) VALUES (
    @oid, @pref, @pts,
    @awei, @acrc, @srate, @cfactor,
    @drate, @xrate, @eeur,
    @pdate, @psrc, @gen
) ON CONFLICT (order_id) DO UPDATE SET
    payment_reference = EXCLUDED.payment_reference,
    payment_timestamp = EXCLUDED.payment_timestamp,
    amount_wei = EXCLUDED.amount_wei,
    amount_crc = EXCLUDED.amount_crc,
    scrc_xdai_rate = EXCLUDED.scrc_xdai_rate,
    conversion_factor = EXCLUDED.conversion_factor,
    dcrc_xdai_rate = EXCLUDED.dcrc_xdai_rate,
    xdai_eur_rate = EXCLUDED.xdai_eur_rate,
    eur_equivalent = EXCLUDED.eur_equivalent,
    price_date = EXCLUDED.price_date,
    pricing_source = EXCLUDED.pricing_source,
    generated_at = EXCLUDED.generated_at;
";
        cmd.Parameters.AddWithValue("@oid", snapshot.OrderId);
        cmd.Parameters.AddWithValue("@pref", snapshot.PaymentReference);
        cmd.Parameters.AddWithValue("@pts", (object?)snapshot.PaymentTimestamp ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@awei", (object?)snapshot.AmountWei ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@acrc", (object?)snapshot.AmountCrc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@srate", (object?)snapshot.ScrToXdaiRate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cfactor", (object?)snapshot.ConversionFactor ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@drate", (object?)snapshot.DcrcToXdaiRate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@xrate", (object?)snapshot.XdaiToEurRate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@eeur", (object?)snapshot.EurEquivalent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@pdate", (object?)snapshot.PriceDate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@psrc", (object?)snapshot.PricingSource ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@gen", snapshot.GeneratedAt);

        await cmd.ExecuteNonQueryAsync(ct);
        _log.LogInformation("Stored conversion snapshot for order {OrderId}", snapshot.OrderId);
    }

    public async Task<ConversionSnapshot?> GetByOrderIdAsync(string orderId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT order_id, payment_reference, payment_timestamp,
       amount_wei, amount_crc, scrc_xdai_rate, conversion_factor,
       dcrc_xdai_rate, xdai_eur_rate, eur_equivalent,
       price_date, pricing_source, generated_at
FROM conversion_snapshots
WHERE order_id = @id";
        cmd.Parameters.AddWithValue("@id", orderId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new ConversionSnapshot
        {
            OrderId = reader.GetString(0),
            PaymentReference = reader.GetString(1),
            PaymentTimestamp = reader.IsDBNull(2) ? null : reader.GetString(2),
            AmountWei = reader.IsDBNull(3) ? null : reader.GetString(3),
            AmountCrc = reader.IsDBNull(4) ? null : reader.GetDecimal(4),
            ScrToXdaiRate = reader.IsDBNull(5) ? null : reader.GetDecimal(5),
            ConversionFactor = reader.IsDBNull(6) ? null : reader.GetDecimal(6),
            DcrcToXdaiRate = reader.IsDBNull(7) ? null : reader.GetDecimal(7),
            XdaiToEurRate = reader.IsDBNull(8) ? null : reader.GetDecimal(8),
            EurEquivalent = reader.IsDBNull(9) ? null : reader.GetDecimal(9),
            PriceDate = reader.IsDBNull(10) ? null : reader.GetString(10),
            PricingSource = reader.IsDBNull(11) ? null : reader.GetString(11),
            GeneratedAt = reader.GetFieldValue<DateTimeOffset>(12)
        };
    }
}