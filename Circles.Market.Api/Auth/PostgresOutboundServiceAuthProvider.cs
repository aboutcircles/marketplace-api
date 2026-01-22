using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Circles.Market.Api.Auth;

public sealed class PostgresOutboundServiceAuthProvider : IOutboundServiceAuthProvider
{
    private readonly string _connString;
    private readonly ILogger<PostgresOutboundServiceAuthProvider> _log;
    private readonly IMemoryCache _cache;

    public PostgresOutboundServiceAuthProvider(string connString, ILogger<PostgresOutboundServiceAuthProvider> log, IMemoryCache cache)
    {
        _connString = connString ?? throw new ArgumentNullException(nameof(connString));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        _log.LogInformation("EnsureSchemaAsync starting for outbound_service_credentials...");
        try
        {
            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);
            _log.LogInformation("Postgres connection opened for outbound schema creation.");
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"CREATE TABLE IF NOT EXISTS outbound_service_credentials (
  id uuid PRIMARY KEY,
  service_kind  text NOT NULL,
  endpoint_origin text NOT NULL,
  path_prefix   text NULL,
  seller_address text NULL,
  chain_id      bigint NULL,
  header_name   text NOT NULL DEFAULT 'X-Circles-Service-Key',
  api_key       text NOT NULL,
  enabled       boolean NOT NULL DEFAULT true,
  created_at    timestamptz NOT NULL DEFAULT now(),
  revoked_at    timestamptz NULL
);

CREATE INDEX IF NOT EXISTS ix_outbound_creds_lookup
  ON outbound_service_credentials(service_kind, endpoint_origin, enabled);";
            await cmd.ExecuteNonQueryAsync(ct);
            _log.LogInformation("EnsureSchemaAsync completed successfully for outbound_service_credentials.");
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "FATAL: EnsureSchemaAsync failed for outbound_service_credentials.");
            throw;
        }
    }

    public async Task<(string headerName, string apiKey)?> TryGetHeaderAsync(Uri endpoint, string serviceKind, string? sellerAddress, long chainId, CancellationToken ct = default)
    {
        if (endpoint is null) throw new ArgumentNullException(nameof(endpoint));
        if (string.IsNullOrWhiteSpace(serviceKind)) throw new ArgumentException("serviceKind is required", nameof(serviceKind));

        var origin = NormalizeOrigin(endpoint);
        var path = endpoint.AbsolutePath ?? "/";
        sellerAddress = string.IsNullOrWhiteSpace(sellerAddress) ? null : sellerAddress.Trim().ToLowerInvariant();

        string cacheKey = $"osa:{serviceKind}:{origin}:{sellerAddress ?? "_"}:{chainId}:{path}";
        if (_cache.TryGetValue<(string headerName, string apiKey)?>(cacheKey, out var cached))
        {
            return cached;
        }

        var candidates = new List<Row>();
        await using (var conn = new NpgsqlConnection(_connString))
        {
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT id, service_kind, endpoint_origin, path_prefix, seller_address, chain_id, header_name, api_key, enabled, created_at
FROM outbound_service_credentials
WHERE enabled = true AND revoked_at IS NULL AND service_kind = $1 AND endpoint_origin = $2";
            cmd.Parameters.AddWithValue(serviceKind);
            cmd.Parameters.AddWithValue(origin);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var rowId = reader.GetGuid(0);
                var headerName = reader.GetString(6);
                var apiKey = reader.GetString(7);

                if (!IsValidHeaderName(headerName) || !IsValidHeaderValue(apiKey))
                {
                    _log.LogWarning("Invalid outbound credential row ignored (id={Id}, kind={Kind}, origin={Origin})", 
                        rowId, serviceKind, origin);
                    continue;
                }

                var row = new Row
                {
                    Id = rowId,
                    ServiceKind = reader.GetString(1),
                    EndpointOrigin = reader.GetString(2),
                    PathPrefix = reader.IsDBNull(3) ? null : reader.GetString(3),
                    SellerAddress = reader.IsDBNull(4) ? null : reader.GetString(4)?.ToLowerInvariant(),
                    ChainId = reader.IsDBNull(5) ? (long?)null : reader.GetInt64(5),
                    HeaderName = headerName,
                    ApiKey = apiKey,
                    Enabled = reader.GetBoolean(8),
                    CreatedAt = reader.GetDateTime(9)
                };
                // Apply seller/chain and prefix filtering in-memory
                if (row.SellerAddress != null && row.SellerAddress != sellerAddress) continue;
                if (row.ChainId != null && row.ChainId.Value != chainId) continue;
                if (!string.IsNullOrEmpty(row.PathPrefix) && !path.StartsWith(row.PathPrefix, StringComparison.Ordinal)) continue;
                candidates.Add(row);
            }
        }

        if (candidates.Count == 0)
        {
            _log.LogWarning("TryGetHeaderAsync: No enabled outbound credentials found for kind={Kind} origin={Origin} seller={Seller} chain={Chain} path={Path}", 
                serviceKind, origin, sellerAddress ?? "_", chainId, path);
            _cache.Set<(string headerName, string apiKey)?>(cacheKey, null, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2), Size = 100 });
            return null;
        }

        // Pick most specific: seller > null, chain > null, longest path_prefix, newest created_at
        var selected = candidates
            .OrderByDescending(r => r.SellerAddress != null)
            .ThenByDescending(r => r.ChainId != null)
            .ThenByDescending(r => r.PathPrefix?.Length ?? 0)
            .ThenByDescending(r => r.CreatedAt)
            .ToList();

        // Check tie ambiguity: compare by the ranking keys
        bool ambiguous = selected.Count > 1 && IsAmbiguous(selected);
        if (ambiguous)
        {
            _log.LogError("TryGetHeaderAsync: Ambiguous selection for kind={Kind} origin={Origin} seller={Seller} chain={Chain} path={Path}. Found {Count} candidates.", 
                serviceKind, origin, sellerAddress ?? "_", chainId, path, candidates.Count);
            _cache.Set<(string headerName, string apiKey)?>(cacheKey, null, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1), Size = 100 });
            return null;
        }

        var pick = selected[0];
        _log.LogInformation("TryGetHeaderAsync: Resolved credential (id={Id}) for kind={Kind} origin={Origin} seller={Seller} chain={Chain} path={Path}", 
            pick.Id, serviceKind, origin, sellerAddress ?? "_", chainId, path);
        var result = (pick.HeaderName, pick.ApiKey);
        _cache.Set(cacheKey, result, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5), Size = 100 });
        return result;
    }

    private static bool IsValidHeaderName(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        // HTTP token charset: ^[!#$%&'*+\-.^_`|~0-9A-Za-z]+$
        return System.Text.RegularExpressions.Regex.IsMatch(s, "^[!#$%&'*+\\-.^_`|~0-9A-Za-z]+$");
    }

    private static bool IsValidHeaderValue(string s)
    {
        if (s == null) return false;
        return !s.Contains('\r') && !s.Contains('\n');
    }

    private static bool IsAmbiguous(List<Row> list)
    {
        if (list.Count < 2) return false;
        var a = list[0];
        var b = list[1];
        if ((a.SellerAddress != null) != (b.SellerAddress != null)) return false;
        if ((a.ChainId != null) != (b.ChainId != null)) return false;
        if ((a.PathPrefix?.Length ?? 0) != (b.PathPrefix?.Length ?? 0)) return false;
        // Same specificity, treat as ambiguous
        return true;
    }

    private static string NormalizeOrigin(Uri uri)
    {
        var scheme = uri.Scheme.ToLowerInvariant();
        var host = uri.Host.ToLowerInvariant();
        int port = uri.IsDefaultPort ? (scheme == "https" ? 443 : 80) : uri.Port;
        return $"{scheme}://{host}:{port}";
    }

    private sealed class Row
    {
        public Guid Id { get; set; }
        public string ServiceKind { get; set; } = string.Empty;
        public string EndpointOrigin { get; set; } = string.Empty;
        public string? PathPrefix { get; set; }
        public string? SellerAddress { get; set; }
        public long? ChainId { get; set; }
        public string HeaderName { get; set; } = "X-Circles-Service-Key";
        public string ApiKey { get; set; } = string.Empty;
        public bool Enabled { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
