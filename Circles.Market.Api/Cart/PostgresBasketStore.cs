using System.Text.Json;
using Npgsql;

namespace Circles.Market.Api.Cart;

public class PostgresBasketStore : IBasketStore
{
    private readonly string _connString;
    private readonly ILogger<PostgresBasketStore> _logger;

    public PostgresBasketStore(string connString, ILogger<PostgresBasketStore> logger)
    {
        _connString = connString;
        _logger = logger;
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        _logger.LogInformation("EnsureSchema starting for baskets...");
        try
        {
            using var conn = new NpgsqlConnection(_connString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS baskets (
  basket_id   text PRIMARY KEY,
  basket_json jsonb NOT NULL,
  status      text NOT NULL,
  modified_at timestamptz NOT NULL,
  expires_at  timestamptz NOT NULL
);";
            cmd.ExecuteNonQuery();

            // Ensure version column for optimistic operations
            using (var alter = conn.CreateCommand())
            {
                alter.CommandText = "ALTER TABLE baskets ADD COLUMN IF NOT EXISTS version bigint NOT NULL DEFAULT 0;";
                alter.ExecuteNonQuery();
            }

            // Helpful index for expiry lookups (future use)
            using var idx = conn.CreateCommand();
            idx.CommandText = "CREATE INDEX IF NOT EXISTS ix_baskets_expires ON baskets (expires_at);";
            idx.ExecuteNonQuery();
            _logger.LogInformation("EnsureSchema completed for baskets.");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "FATAL: Failed to ensure Postgres schema for baskets");
            throw;
        }
    }

    private static string NewId(string prefix)
        => prefix + Guid.NewGuid().ToString("N").Substring(0, 22);

    public Basket Create(string? operatorAddr, string? buyerAddr, long? chainId)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string id = NewId("bkt_");
        var b = new Basket
        {
            BasketId = id,
            Operator = operatorAddr,
            Buyer = buyerAddr,
            ChainId = chainId ?? 100,
            Status = nameof(BasketStatus.Draft),
            CreatedAt = now,
            ModifiedAt = now,
            TtlSeconds = 86400,
            Version = 0
        };

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(now).AddSeconds(b.TtlSeconds);

        using var conn = new NpgsqlConnection(_connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO baskets (basket_id, basket_json, status, modified_at, expires_at) VALUES (@id, @json, @status, @mod, @exp)";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@json", NpgsqlTypes.NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(b, Circles.Profiles.Models.JsonSerializerOptions.JsonLd));
        cmd.Parameters.AddWithValue("@status", b.Status);
        cmd.Parameters.AddWithValue("@mod", DateTimeOffset.FromUnixTimeSeconds(now));
        cmd.Parameters.AddWithValue("@exp", expiresAt);
        cmd.ExecuteNonQuery();
        return b;
    }

    public (Basket basket, bool expired)? Get(string basketId)
    {
        _logger.LogInformation("Getting basket {BasketId}", basketId);
        using var conn = new NpgsqlConnection(_connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT basket_json, expires_at, status, version FROM baskets WHERE basket_id = @id";
        cmd.Parameters.AddWithValue("@id", basketId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            _logger.LogWarning("Basket {BasketId} not found", basketId);
            return null;
        }

        string json = reader.GetString(0);
        var expiresAt = reader.GetFieldValue<DateTimeOffset>(1);
        string status = reader.GetString(2);

        var basket = JsonSerializer.Deserialize<Basket>(
            json,
            Circles.Profiles.Models.JsonSerializerOptions.JsonLd);

        if (basket is null)
        {
            _logger.LogError("basket_json for {BasketId} deserialized to null", basketId);
            throw new InvalidOperationException("basket_json deserialized to null");
        }

        // Source-of-truth: columns override JSON
        basket.Status = status;
        long version = reader.GetInt64(3);
        basket.Version = version;

        bool expired = DateTimeOffset.UtcNow >= expiresAt ||
                       string.Equals(status, nameof(BasketStatus.Expired), StringComparison.Ordinal);

        _logger.LogInformation("Basket {BasketId} retrieved. Status: {Status}, Expired: {Expired}", basketId, status, expired);
        return (basket, expired);
    }

    public Basket Patch(string basketId, Action<Basket> patch)
    {
        // Load
        var res = Get(basketId);
        if (res is null) throw new KeyNotFoundException();
        var (b, expired) = res.Value;
        if (string.Equals(b.Status, nameof(BasketStatus.CheckedOut), StringComparison.Ordinal))
            throw new InvalidOperationException("Basket already checked out");

        // Apply patch
        patch(b);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        b.ModifiedAt = now;
        b.Version++;
        var newExpires = DateTimeOffset.UtcNow.AddSeconds(b.TtlSeconds);

        // Save
        using var conn = new NpgsqlConnection(_connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE baskets
SET basket_json = @json,
    modified_at = @mod,
    expires_at  = @exp,
    version = version + 1
WHERE basket_id = @id
  AND status <> @checkedOut;";
        cmd.Parameters.AddWithValue("@id", basketId);
        cmd.Parameters.AddWithValue("@json", NpgsqlTypes.NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(b, Circles.Profiles.Models.JsonSerializerOptions.JsonLd));
        cmd.Parameters.AddWithValue("@mod", DateTimeOffset.FromUnixTimeSeconds(now));
        cmd.Parameters.AddWithValue("@exp", newExpires);
        cmd.Parameters.AddWithValue("@checkedOut", nameof(BasketStatus.CheckedOut));
        int n = cmd.ExecuteNonQuery();
        if (n == 0) throw new InvalidOperationException("Basket already checked out");
        return b;
    }

    public bool TryFreeze(string basketId)
    {
        using var conn = new NpgsqlConnection(_connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE baskets
SET status = @status, modified_at = @mod
WHERE basket_id = @id
  AND status <> @checkedOut;";
        cmd.Parameters.AddWithValue("@id", basketId);
        cmd.Parameters.AddWithValue("@status", nameof(BasketStatus.CheckedOut));
        cmd.Parameters.AddWithValue("@checkedOut", nameof(BasketStatus.CheckedOut));
        cmd.Parameters.AddWithValue("@mod", DateTimeOffset.UtcNow);

        int affected = cmd.ExecuteNonQuery();
        bool updated = affected > 0;
        return updated;
    }

    public Basket? TryFreezeAndRead(string basketId)
    {
        using var conn = new NpgsqlConnection(_connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE baskets
SET status = @status, modified_at = @mod, version = version + 1
WHERE basket_id = @id
  AND status <> @checkedOut
RETURNING basket_json, version;";
        cmd.Parameters.AddWithValue("@id", basketId);
        cmd.Parameters.AddWithValue("@status", nameof(BasketStatus.CheckedOut));
        cmd.Parameters.AddWithValue("@checkedOut", nameof(BasketStatus.CheckedOut));
        cmd.Parameters.AddWithValue("@mod", DateTimeOffset.UtcNow);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        string json = reader.GetString(0);
        long version = reader.GetInt64(1);
        var basket = JsonSerializer.Deserialize<Basket>(json, Circles.Profiles.Models.JsonSerializerOptions.JsonLd);
        if (basket is null)
        {
            throw new InvalidOperationException("basket_json deserialized to null");
        }

        basket.Status = nameof(BasketStatus.CheckedOut);
        basket.Version = version;
        return basket;
    }

    public Basket? TryFreezeAndRead(string basketId, long expectedVersion)
    {
        using var conn = new NpgsqlConnection(_connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE baskets
SET status = @status, modified_at = @mod, version = version + 1
WHERE basket_id = @id
  AND status <> @checkedOut
  AND version = @expected
RETURNING basket_json, version;";
        cmd.Parameters.AddWithValue("@id", basketId);
        cmd.Parameters.AddWithValue("@status", nameof(BasketStatus.CheckedOut));
        cmd.Parameters.AddWithValue("@checkedOut", nameof(BasketStatus.CheckedOut));
        cmd.Parameters.AddWithValue("@expected", expectedVersion);
        cmd.Parameters.AddWithValue("@mod", DateTimeOffset.UtcNow);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        string json = reader.GetString(0);
        long version = reader.GetInt64(1);
        var basket = JsonSerializer.Deserialize<Basket>(json, Circles.Profiles.Models.JsonSerializerOptions.JsonLd);
        if (basket is null)
        {
            throw new InvalidOperationException("basket_json deserialized to null");
        }

        basket.Status = nameof(BasketStatus.CheckedOut);
        basket.Version = version;
        return basket;
    }
}
