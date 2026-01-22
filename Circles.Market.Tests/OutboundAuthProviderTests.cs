using Circles.Market.Api.Auth;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Circles.Market.Tests;

[TestFixture]
public class OutboundAuthProviderTests
{
    private string? _connString;

    [OneTimeSetUp]
    public void Setup()
    {
        _connString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION");
        if (string.IsNullOrEmpty(_connString))
        {
            Assert.Ignore("POSTGRES_CONNECTION not set, skipping auth provider tests.");
        }
    }

    [Test]
    public async Task TryGetHeaderAsync_InvalidHeaderName_IsIgnored()
    {
        var authProvider = new PostgresOutboundServiceAuthProvider(_connString!, NullLogger<PostgresOutboundServiceAuthProvider>.Instance, new MemoryCache(new MemoryCacheOptions()));
        await authProvider.EnsureSchemaAsync();

        var id = Guid.NewGuid();
        var origin = "https://invalid-header.com:443";
        await InsertCredential(id, "inventory", origin, "X-Invalid\r\nName", "key123");

        var result = await authProvider.TryGetHeaderAsync(new Uri(origin), "inventory", null, 0);
        Assert.That(result, Is.Null, "Credential with invalid header name should be ignored");
    }

    [Test]
    public async Task TryGetHeaderAsync_InvalidApiKey_IsIgnored()
    {
        var authProvider = new PostgresOutboundServiceAuthProvider(_connString!, NullLogger<PostgresOutboundServiceAuthProvider>.Instance, new MemoryCache(new MemoryCacheOptions()));
        await authProvider.EnsureSchemaAsync();

        var id = Guid.NewGuid();
        var origin = "https://invalid-key.com:443";
        await InsertCredential(id, "inventory", origin, "X-Valid-Name", "key\nwith\nnewline");

        var result = await authProvider.TryGetHeaderAsync(new Uri(origin), "inventory", null, 0);
        Assert.That(result, Is.Null, "Credential with invalid API key should be ignored");
    }

    private async Task InsertCredential(Guid id, string kind, string origin, string header, string key)
    {
        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO outbound_service_credentials (id, service_kind, endpoint_origin, header_name, api_key, enabled)
                            VALUES ($1, $2, $3, $4, $5, true)";
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(kind);
        cmd.Parameters.AddWithValue(origin);
        cmd.Parameters.AddWithValue(header);
        cmd.Parameters.AddWithValue(key);
        await cmd.ExecuteNonQueryAsync();
    }
}
