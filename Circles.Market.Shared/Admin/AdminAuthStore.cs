using Microsoft.Extensions.Logging;
using Npgsql;

namespace Circles.Market.Shared.Admin;

public interface IAdminAuthChallengeStore
{
    Task SaveAsync(AdminAuthChallenge ch, CancellationToken ct = default);
    Task<AdminAuthChallenge?> GetAsync(Guid id, CancellationToken ct = default);
    Task<bool> TryMarkUsedAsync(Guid id, CancellationToken ct = default);
}

public sealed class PostgresAdminAuthChallengeStore : IAdminAuthChallengeStore
{
    private readonly string _connString;
    private readonly ILogger<PostgresAdminAuthChallengeStore> _log;

    public PostgresAdminAuthChallengeStore(string connString, ILogger<PostgresAdminAuthChallengeStore> log)
    {
        _connString = connString ?? throw new ArgumentNullException(nameof(connString));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        try
        {
            using var conn = new NpgsqlConnection(_connString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS admin_auth_challenges (
  id uuid PRIMARY KEY,
  address text NOT NULL,
  chain_id bigint NOT NULL,
  nonce text NOT NULL,
  message text NOT NULL,
  issued_at timestamptz NOT NULL,
  expires_at timestamptz NOT NULL,
  used_at timestamptz NULL,
  user_agent text NULL,
  ip text NULL
);";
            cmd.ExecuteNonQuery();

            using var ix = conn.CreateCommand();
            ix.CommandText = "CREATE INDEX IF NOT EXISTS ix_admin_auth_addr_nonce ON admin_auth_challenges (address, nonce);";
            ix.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to ensure admin_auth_challenges schema");
            throw;
        }
    }

    public async Task SaveAsync(AdminAuthChallenge ch, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO admin_auth_challenges (id, address, chain_id, nonce, message, issued_at, expires_at, user_agent, ip)
VALUES (@id, @addr, @chain, @nonce, @msg, @iat, @exp, @ua, @ip)";
        cmd.Parameters.AddWithValue("@id", ch.Id);
        cmd.Parameters.AddWithValue("@addr", ch.Address.ToLowerInvariant());
        cmd.Parameters.AddWithValue("@chain", ch.ChainId);
        cmd.Parameters.AddWithValue("@nonce", ch.Nonce);
        cmd.Parameters.AddWithValue("@msg", ch.Message);
        cmd.Parameters.AddWithValue("@iat", ch.IssuedAt);
        cmd.Parameters.AddWithValue("@exp", ch.ExpiresAt);
        cmd.Parameters.AddWithValue("@ua", (object?)ch.UserAgent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ip", (object?)ch.Ip ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<AdminAuthChallenge?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT address, chain_id, nonce, message, issued_at, expires_at, used_at, user_agent, ip FROM admin_auth_challenges WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new AdminAuthChallenge
        {
            Id = id,
            Address = reader.GetString(0),
            ChainId = reader.GetInt64(1),
            Nonce = reader.GetString(2),
            Message = reader.GetString(3),
            IssuedAt = reader.GetFieldValue<DateTimeOffset>(4),
            ExpiresAt = reader.GetFieldValue<DateTimeOffset>(5),
            UsedAt = reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset?>(6),
            UserAgent = reader.IsDBNull(7) ? null : reader.GetString(7),
            Ip = reader.IsDBNull(8) ? null : reader.GetString(8)
        };
    }

    public async Task<bool> TryMarkUsedAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE admin_auth_challenges
SET used_at = now()
WHERE id = @id
  AND used_at IS NULL
  AND expires_at > now();";
        cmd.Parameters.AddWithValue("@id", id);
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        return rows == 1;
    }
}
