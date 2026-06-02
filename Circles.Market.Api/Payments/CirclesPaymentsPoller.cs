using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Npgsql;
using System.Numerics;

namespace Circles.Market.Api.Payments;

// circles_query request/response contracts (minimal)
internal sealed class CirclesQueryRequest
{
    public string Namespace { get; set; } = "CrcV2";
    public string Table { get; set; } = "PaymentReceived";
    public List<object> Columns { get; set; } = new();
    public List<object> Filter { get; set; } = new();
    public List<OrderSpec> Order { get; set; } = new();
    public int Limit { get; set; } = 1000;
}

internal sealed class OrderSpec
{
    public string Column { get; set; } = "";
    public string SortOrder { get; set; } = "ASC";
}

internal sealed class CirclesQueryEnvelope<T>
{
    public string Jsonrpc { get; set; } = "2.0";
    public int Id { get; set; } = 1;
    public string Method { get; set; } = "circles_query";
    public T[] Params { get; set; } = Array.Empty<T>();
}

internal sealed class CirclesQueryResult
{
    public string[] Columns { get; set; } = Array.Empty<string>();
    public object[][] Rows { get; set; } = Array.Empty<object[]>();
}

internal sealed class CirclesQueryResponse
{
    public CirclesQueryResult? Result { get; set; }

    // Some JSON-RPC servers may return null or non-numeric ids; keep this flexible to avoid deserialization errors
    public object? Id { get; set; }
    public object? Error { get; set; }
}

public sealed class CirclesPaymentsPoller : BackgroundService
{
    private readonly ILogger<CirclesPaymentsPoller> _log;
    private readonly IHttpClientFactory _hcf;
    private readonly IOrderPaymentFlow _paymentFlow;
    private readonly IPaymentStore _payments;
    private readonly string _rpcUrl;
    private readonly string _circlesRpcUrl;
    private readonly string _pgConn;
    private readonly long _chainId;
    private readonly TimeSpan _interval;
    private readonly int _pageSize;
    private readonly HashSet<string>? _gatewayAllowList; // lowercased
    private readonly int _confirmConfirmations;
    private readonly int _finalizeConfirmations;
    private readonly TimeSpan _trustCacheTtl;
    // gateway (lowercase) -> (active trusted avatar set, fetchedAt). Short-TTL cache so we don't
    // re-query the gateway's on-chain trust list on every transfer.
    private readonly Dictionary<string, (HashSet<string> Set, DateTimeOffset FetchedAt)> _trustCache = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public CirclesPaymentsPoller(
        ILogger<CirclesPaymentsPoller> log,
        IHttpClientFactory hcf,
        IOrderPaymentFlow paymentFlow,
        IPaymentStore payments)
    {
        _log = log;
        _hcf = hcf;
        _paymentFlow = paymentFlow ?? throw new ArgumentNullException(nameof(paymentFlow));
        _payments = payments ?? throw new ArgumentNullException(nameof(payments));
        // Configuration consistency: read exclusively from environment variables (same as Program.cs)
        _rpcUrl = Environment.GetEnvironmentVariable("RPC")
                  ?? throw new Exception("RPC env variable is required for payments poller");
        // CIRCLES_RPC is optional; if not set, use RPC for circles_query calls
        _circlesRpcUrl = Environment.GetEnvironmentVariable("CIRCLES_RPC") ?? _rpcUrl;
        _pgConn = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION")
                  ?? throw new Exception("POSTGRES_CONNECTION env variable is required for payments poller");
        _chainId = long.TryParse(Environment.GetEnvironmentVariable("CHAIN_ID"), out var chain)
            ? chain
            : 100L;
        var pollSeconds = int.TryParse(Environment.GetEnvironmentVariable("POLL_SECONDS"), out var s2) ? s2 : 5;
        _interval = TimeSpan.FromSeconds(Math.Max(1, pollSeconds));
        var pageSize = int.TryParse(Environment.GetEnvironmentVariable("PAGE_SIZE"), out var p2) ? p2 : 500;
        _pageSize = Math.Clamp(pageSize, 1, 1000);

        // Number of confirmations required to consider a payment confirmed/finalized.
        // Defaults: confirmed=3, finalized=12
        _confirmConfirmations = int.TryParse(Environment.GetEnvironmentVariable("CONFIRM_CONFIRMATIONS"), out var conf)
            ? Math.Max(0, conf)
            : 3;

        _finalizeConfirmations = int.TryParse(Environment.GetEnvironmentVariable("FINALIZE_CONFIRMATIONS"), out var fin)
            ? Math.Max(0, fin)
            : 12;

        if (_finalizeConfirmations > 0 && _confirmConfirmations > _finalizeConfirmations)
        {
            _log.LogWarning(
                "CONFIRM_CONFIRMATIONS ({Confirm}) is greater than FINALIZE_CONFIRMATIONS ({Finalize}); confirmations may skip straight to finalization.",
                _confirmConfirmations, _finalizeConfirmations);
        }

        var gatewaysCsv = Environment.GetEnvironmentVariable("PAYMENT_GATEWAYS");
        if (!string.IsNullOrWhiteSpace(gatewaysCsv))
        {
            _gatewayAllowList = new HashSet<string>(gatewaysCsv
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(a => a.ToLowerInvariant()));
        }

        var trustTtl = int.TryParse(Environment.GetEnvironmentVariable("PAYMENT_TRUST_CACHE_TTL_SECONDS"), out var ttl)
            ? ttl
            : 60;
        _trustCacheTtl = TimeSpan.FromSeconds(Math.Max(1, trustTtl));

        bool allowListEmpty = _gatewayAllowList is null || _gatewayAllowList.Count == 0;
        if (allowListEmpty)
        {
            // Token-trust is always enforced per gateway, but without a gateway allowlist a payment
            // routed through an attacker-controlled gateway that self-trusts a token would still be
            // credited. Set PAYMENT_GATEWAYS (or settle the gateway-authorization model) to close this.
            _log.LogWarning(
                "PAYMENT_GATEWAYS is empty: payment events from ANY gateway are processed. Token-trust " +
                "is still enforced per gateway, but an attacker-controlled gateway that self-trusts a token " +
                "would be credited. Set PAYMENT_GATEWAYS to restrict authoritative gateways.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await EnsureCursorTableAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to ensure payments_cursors table");
            throw;
        }

        var http = _hcf.CreateClient();
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(http, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Payments poller tick failed");
            }

            try
            {
                await Task.Delay(_interval, stoppingToken);
            }
            catch
            {
                break;
            }
        }
    }

    private async Task TickAsync(HttpClient http, CancellationToken ct)
    {
        // 1) Ingest any new PaymentReceived logs (non-fatal if none)
        PaymentTransferRecord? lastTransfer = null;
        int processed = 0;
        int observedRowCount = 0;

        bool sawAnyRows = false;
        long lastSeenBlock = 0;
        int lastSeenTx = 0;
        int lastSeenLog = 0;

        var (lastBlock, lastTx, lastLog) = await LoadCursorAsync(ct);
        var reqModel = BuildQuery(lastBlock, lastTx, lastLog);
        var env = new CirclesQueryEnvelope<CirclesQueryRequest> { Params = new[] { reqModel } };

        // Use CIRCLES_RPC for circles_query calls (may differ from RPC for eth_* calls)
        using (var req = new HttpRequestMessage(HttpMethod.Post, _circlesRpcUrl)
               {
                   Content = new StringContent(JsonSerializer.Serialize(env, JsonSerializerOptions.Web), Encoding.UTF8,
                       "application/json")
               })
        using (var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync(ct);
            var parsed = JsonSerializer.Deserialize<CirclesQueryResponse>(body, JsonOpts);
            var result = parsed?.Result;

            if (result is not null && result.Rows.Length > 0)
            {
                observedRowCount = result.Rows.Length;
                var cols = result.Columns;
                foreach (var row in result.Rows)
                {
                    if (TryExtractCursor(cols, row, out var seenBlock, out var seenTx, out var seenLog))
                    {
                        sawAnyRows = true;
                        lastSeenBlock = seenBlock;
                        lastSeenTx = seenTx;
                        lastSeenLog = seenLog;
                    }

                    var transfer = RowToPaymentTransfer(cols, row);
                    if (transfer is null) continue;
                    if (_gatewayAllowList != null &&
                        !_gatewayAllowList.Contains(transfer.GatewayAddress.ToLowerInvariant()))
                        continue;

                    // Require non-empty payment reference and non-negative amount
                    if (string.IsNullOrWhiteSpace(transfer.PaymentReference) ||
                        (transfer.AmountWei.HasValue && transfer.AmountWei.Value < 0))
                        continue;

                    // Resolve token-trust eligibility against the receiving gateway's trust list and
                    // annotate the transfer; the order payment flow credits only eligible (trusted) transfers.
                    var trustSet = await GetGatewayTrustSetAsync(http, transfer.GatewayAddress, ct);
                    transfer = transfer with { Eligible = DecideEligibility(trustSet, transfer.TokenAvatar) };

                    await _paymentFlow.HandleObservedTransferAsync(transfer, ct);
                    lastTransfer = transfer;
                    processed++;
                }
            }
        }

        // IMPORTANT: Advance cursor based on the *last row observed*, even if the row is skipped
        // (gateway filter, malformed data, missing payment reference, etc). Otherwise the poller
        // can get stuck forever re-reading the same earliest unseen row.
        if (sawAnyRows)
        {
            await SaveCursorAsync(lastSeenBlock, lastSeenTx, lastSeenLog, ct);
        }

        if (processed > 0)
        {
            _log.LogInformation(
                "Payments poller ingested {Count} rows; cursor now {Block}/{Tx}/{Log}",
                processed,
                sawAnyRows ? lastSeenBlock : lastTransfer?.BlockNumber,
                sawAnyRows ? lastSeenTx : lastTransfer?.TransactionIndex,
                sawAnyRows ? lastSeenLog : lastTransfer?.LogIndex);
        }
        else if (sawAnyRows)
        {
            _log.LogInformation(
                "Payments poller observed {Count} rows but processed none (filters/malformed); cursor advanced to {Block}/{Tx}/{Log}",
                observedRowCount,
                lastSeenBlock,
                lastSeenTx,
                lastSeenLog);
        }

        // 2) Resolve any transfers whose token-trust eligibility was previously undetermined
        // (e.g. trust data was briefly unavailable). A flip to eligible can complete an order.
        try
        {
            await ReevaluateUndeterminedAsync(http, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ReevaluateUndeterminedAsync failed");
        }

        // 3) Regardless of ingestion, try to confirm and finalize eligible payments
        try
        {
            await ConfirmEligibleAsync(http, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ConfirmEligibleAsync failed");
        }

        try
        {
            await FinalizeEligibleAsync(http, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "FinalizeEligibleAsync failed");
        }
    }

    private CirclesQueryRequest BuildQuery(long lastBlock, int lastTxIndex, int lastLogIndex)
    {
        var filter = new List<object>();
        if (lastBlock >= 0)
        {
            // Lexicographic strictly-greater-than over (blockNumber, transactionIndex, logIndex)
            filter.Add(new
            {
                Type = "Conjunction",
                ConjunctionType = "Or",
                Predicates = new object[]
                {
                    new
                    {
                        Type = "FilterPredicate", FilterType = "GreaterThan", Column = "blockNumber", Value = lastBlock
                    },
                    new
                    {
                        Type = "Conjunction", ConjunctionType = "And", Predicates = new object[]
                        {
                            new
                            {
                                Type = "FilterPredicate", FilterType = "Equals", Column = "blockNumber",
                                Value = lastBlock
                            },
                            new
                            {
                                Type = "FilterPredicate", FilterType = "GreaterThan", Column = "transactionIndex",
                                Value = lastTxIndex
                            }
                        }
                    },
                    new
                    {
                        Type = "Conjunction", ConjunctionType = "And", Predicates = new object[]
                        {
                            new
                            {
                                Type = "FilterPredicate", FilterType = "Equals", Column = "blockNumber",
                                Value = lastBlock
                            },
                            new
                            {
                                Type = "FilterPredicate", FilterType = "Equals", Column = "transactionIndex",
                                Value = lastTxIndex
                            },
                            new
                            {
                                Type = "FilterPredicate", FilterType = "GreaterThan", Column = "logIndex",
                                Value = lastLogIndex
                            }
                        }
                    }
                }
            });
        }

        // Optional server-side gateway filter (as OR group)
        if (_gatewayAllowList is { Count: > 0 })
        {
            filter.Add(new
            {
                Type = "Conjunction",
                ConjunctionType = "Or",
                Predicates = _gatewayAllowList.Select(g => new
                    { Type = "FilterPredicate", FilterType = "Equals", Column = "gateway", Value = g }).ToArray()
            });
        }

        return new CirclesQueryRequest
        {
            Namespace = "CrcV2_PaymentGateway",
            Table = "PaymentReceived",
            Columns = new(),
            Filter = filter,
            Order = new()
            {
                new OrderSpec { Column = "blockNumber", SortOrder = "ASC" },
                new OrderSpec { Column = "transactionIndex", SortOrder = "ASC" },
                new OrderSpec { Column = "logIndex", SortOrder = "ASC" }
            },
            Limit = _pageSize
        };
    }

    private async Task FinalizeEligibleAsync(HttpClient http, CancellationToken ct)
    {
        // Fetch current head block via eth_blockNumber
        long head;
        try
        {
            head = await GetHeadBlockAsync(http, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to fetch head block for finalization step");
            return;
        }

        if (_finalizeConfirmations <= 0) return;

        var threshold = head - _finalizeConfirmations;
        if (threshold < 0) return;

        // Query eligible, non-finalized payments with sufficient confirmations
        using var conn = new NpgsqlConnection(_pgConn);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT payment_reference, first_block_number
FROM payments
WHERE chain_id=@c AND status <> 'finalized' AND first_block_number IS NOT NULL AND first_block_number <= @threshold
ORDER BY first_block_number ASC";
        cmd.Parameters.AddWithValue("@c", _chainId);
        cmd.Parameters.AddWithValue("@threshold", threshold);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        var toFinalize = new List<(string paymentRef, long block)>();
        while (await reader.ReadAsync(ct))
        {
            var pref = reader.GetString(0);
            var blk = reader.GetInt64(1);
            toFinalize.Add((pref, blk));
        }

        foreach (var (pref, blk) in toFinalize)
        {
            var finalizedAt = DateTimeOffset.UtcNow;
            await _paymentFlow.HandleFinalizationAsync(_chainId, pref, finalizedAt, ct);
        }
    }

    private async Task ConfirmEligibleAsync(HttpClient http, CancellationToken ct)
    {
        long head;
        try
        {
            head = await GetHeadBlockAsync(http, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to fetch head block for confirmation step");
            return;
        }

        if (_confirmConfirmations <= 0)
        {
            return;
        }

        var threshold = head - _confirmConfirmations;
        if (threshold < 0)
        {
            return;
        }

        using var conn = new NpgsqlConnection(_pgConn);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT payment_reference, first_block_number
FROM payments
WHERE chain_id=@c AND status NOT IN ('confirmed','finalized') AND first_block_number IS NOT NULL AND first_block_number <= @threshold
ORDER BY first_block_number ASC";
        cmd.Parameters.AddWithValue("@c", _chainId);
        cmd.Parameters.AddWithValue("@threshold", threshold);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        var toConfirm = new List<(string pref, long block)>();
        while (await reader.ReadAsync(ct))
        {
            string pref = reader.GetString(0);
            long blk = reader.GetInt64(1);
            toConfirm.Add((pref, blk));
        }

        foreach (var (pref, blk) in toConfirm)
        {
            DateTimeOffset confirmedAt = DateTimeOffset.UtcNow;
            await _paymentFlow.HandleConfirmedAsync(_chainId, pref, blk, confirmedAt, ct);
        }
    }

    private async Task ReevaluateUndeterminedAsync(HttpClient http, CancellationToken ct)
    {
        const int limit = 500;
        // Oldest-first (ASC in the store query) so a backlog drains FIFO and the earliest stuck
        // transfers can never be starved by a steady stream of newer ones.
        var undetermined = _payments.GetUndeterminedTransfers(_chainId, limit).ToList();
        if (undetermined.Count == 0) return;
        if (undetermined.Count >= limit)
        {
            _log.LogWarning(
                "Undetermined payment-transfer backlog hit the re-eval page limit ({Limit}); remaining transfers drain over subsequent ticks",
                limit);
        }

        foreach (var t in undetermined)
        {
            ct.ThrowIfCancellationRequested();

            var trustSet = await GetGatewayTrustSetAsync(http, t.GatewayAddress, ct);
            // Reuse the single eligibility decision; null = still undetermined (no avatar or trust
            // data unavailable) → leave it and retry next tick.
            var verdict = DecideEligibility(trustSet, t.TokenAvatar);
            if (verdict is null) continue;

            bool eligible = verdict.Value;
            _payments.SetTransferEligibility(t.ChainId, t.TxHash, t.LogIndex, eligible);

            // Only a now-eligible transfer can move an order forward; re-run the flow to re-aggregate
            // and attempt settlement. Ineligible stays recorded but uncredited (no flow call needed).
            if (eligible)
            {
                await _paymentFlow.HandleObservedTransferAsync(t with { Eligible = true }, ct);
            }
        }
    }

    public static long ParseEthHexBlock(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            throw new InvalidOperationException("eth_blockNumber returned empty result");
        hex = hex.Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            hex = hex[2..];
        }
        if (hex.Length == 0) return 0L;
        // Parse as unsigned to avoid two's complement negativity for hex with high MSB
        if (!ulong.TryParse(hex, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out var ul))
        {
            // Distinguish overflow from format: if all chars are hex digits, it's an overflow
            bool allHex = true;
            for (int i = 0; i < hex.Length; i++)
            {
                char c = hex[i];
                bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!isHex) { allHex = false; break; }
            }
            if (allHex) throw new OverflowException("Block number exceeds UInt64 range");
            throw new FormatException("Invalid hex value for block number");
        }
        if (ul > long.MaxValue) throw new OverflowException("Block number exceeds Int64 range");
        return (long)ul;
    }

    private async Task<long> GetHeadBlockAsync(HttpClient http, CancellationToken ct)
    {
        // Build JSON-RPC request body without using the reserved identifier 'params'
        var body = "{\"jsonrpc\":\"2.0\",\"method\":\"eth_blockNumber\",\"params\":[],\"id\":1}";
        using var req = new HttpRequestMessage(HttpMethod.Post, _rpcUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var hex = doc.RootElement.GetProperty("result").GetString();
        return ParseEthHexBlock(hex);
    }

    /// <summary>
    /// Active trusted-avatar set for a gateway, cached for _trustCacheTtl. Returns null when the
    /// set cannot be determined (RPC error and no cached value), so the caller leaves the transfer
    /// undetermined and retries later rather than wrongly rejecting it.
    /// </summary>
    private async Task<HashSet<string>?> GetGatewayTrustSetAsync(HttpClient http, string gateway, CancellationToken ct)
    {
        gateway = gateway.ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        if (_trustCache.TryGetValue(gateway, out var cached) && (now - cached.FetchedAt) < _trustCacheTtl)
        {
            return cached.Set;
        }

        try
        {
            var set = await FetchActiveTrustSetAsync(http, gateway, now.ToUnixTimeSeconds(), ct);
            _trustCache[gateway] = (set, now);
            return set;
        }
        catch (Exception ex)
        {
            Metrics.MarketplaceMetrics.GatewayTrustFetchFailures.Inc();
            _log.LogWarning(ex, "Failed to fetch gateway trust set for {Gateway}", gateway);
            // Fall back to stale cache if we have one; else undetermined (null).
            return _trustCache.TryGetValue(gateway, out var stale) ? stale.Set : null;
        }
    }

    private async Task<HashSet<string>> FetchActiveTrustSetAsync(HttpClient http, string gateway, long nowUnix,
        CancellationToken ct)
    {
        var reqModel = new CirclesQueryRequest
        {
            Namespace = "CrcV2_PaymentGateway",
            Table = "TrustUpdated",
            Columns = new(),
            Filter = new List<object>
            {
                new { Type = "FilterPredicate", FilterType = "Equals", Column = "gateway", Value = gateway }
            },
            Order = new()
            {
                new OrderSpec { Column = "blockNumber", SortOrder = "ASC" },
                new OrderSpec { Column = "transactionIndex", SortOrder = "ASC" },
                new OrderSpec { Column = "logIndex", SortOrder = "ASC" }
            },
            Limit = 1000
        };
        var env = new CirclesQueryEnvelope<CirclesQueryRequest> { Params = new[] { reqModel } };

        using var req = new HttpRequestMessage(HttpMethod.Post, _circlesRpcUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(env, JsonSerializerOptions.Web), Encoding.UTF8,
                "application/json")
        };
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct);
        var parsed = JsonSerializer.Deserialize<CirclesQueryResponse>(body, JsonOpts);

        // A fault (JSON-RPC error, missing result, or schema drift) must NOT be mistaken for a
        // genuine "gateway trusts nothing" answer — otherwise a transient RPC problem would cache an
        // empty set and permanently reject every payment for this gateway. Throw so the caller leaves
        // transfers undetermined and retries. Only a successful query (result present, expected columns)
        // is treated as authoritative; zero rows then legitimately means an empty trust list.
        if (parsed?.Error is not null)
            throw new InvalidOperationException($"circles_query TrustUpdated returned a JSON-RPC error for gateway {gateway}: {parsed.Error}");
        var result = parsed?.Result
            ?? throw new InvalidOperationException($"circles_query TrustUpdated returned no result for gateway {gateway}");

        if (result.Rows.Length > 0)
        {
            bool hasTrustCols =
                Array.FindIndex(result.Columns, c => string.Equals(c, "trustReceiver", StringComparison.OrdinalIgnoreCase)) >= 0 &&
                Array.FindIndex(result.Columns, c => string.Equals(c, "expiry", StringComparison.OrdinalIgnoreCase)) >= 0;
            if (!hasTrustCols)
                throw new InvalidOperationException($"circles_query TrustUpdated missing trustReceiver/expiry columns for gateway {gateway} (schema drift)");
        }

        return ParseActiveTrustSet(result.Columns, result.Rows, nowUnix);
    }

    /// <summary>
    /// Decide a transfer's token-trust eligibility against a gateway trust set:
    /// null trust set (unavailable) or missing token avatar → undetermined (null);
    /// otherwise true iff the token's avatar is in the trusted set.
    /// </summary>
    internal static bool? DecideEligibility(HashSet<string>? trustSet, string? tokenAvatar)
    {
        if (string.IsNullOrEmpty(tokenAvatar)) return null;
        if (trustSet is null) return null;
        return trustSet.Contains(NormalizeAvatar(tokenAvatar));
    }

    private PaymentTransferRecord? RowToPaymentTransfer(string[] cols, object[] row)
    {
        int Col(string n) => Array.FindIndex(cols, c => string.Equals(c, n, StringComparison.OrdinalIgnoreCase));

        try
        {
            var ib = Col("blockNumber");
            var its = Col("timestamp");
            var itx = Col("transactionIndex");
            var il = Col("logIndex");
            var ih = Col("transactionHash");
            var igw = Col("gateway");
            var ipayer = Col("payer");
            var iamt = Col("amount");
            var idata = Col("data");
            var itoken = Col("tokenId");

            long block = ToInt64(row[ib]);
            int txIndex = ToInt32(row[itx]);
            int logIndex = ToInt32(row[il]);
            string txHash = ToStringStrict(row[ih]);
            string gateway = ToStringStrict(row[igw]).ToLowerInvariant();
            string? payer = ToStringOrNull(row[ipayer]);

            BigInteger? amount = ParseUintToBigInteger(row[iamt]);

            // ERC1155 token id of the received CRC; resolve to its avatar so we can check gateway trust.
            BigInteger? tokenId = itoken >= 0 && itoken < row.Length ? ParseUintToBigInteger(row[itoken]) : null;
            string? tokenAvatar = tokenId.HasValue ? ToAvatarAddress(tokenId.Value) : null;

            string? rawPaymentRef = TryDecodePaymentRef(row[idata]);
            string? paymentRef = NormalizePaymentReference(rawPaymentRef);

            bool hasPaymentRef = !string.IsNullOrWhiteSpace(paymentRef);
            if (!hasPaymentRef)
            {
                return null;
            }

            return new PaymentTransferRecord(
                ChainId: _chainId,
                TxHash: txHash,
                LogIndex: logIndex,
                TransactionIndex: txIndex,
                BlockNumber: block,
                PaymentReference: paymentRef!,
                GatewayAddress: gateway,
                PayerAddress: payer?.ToLowerInvariant(),
                AmountWei: amount,
                CreatedAt: DateTimeOffset.UtcNow,
                TokenId: tokenId,
                TokenAvatar: tokenAvatar
            );
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to map CrcV2.PaymentReceived row");
            return null;
        }
    }

    internal static bool TryExtractCursor(string[] cols, object[] row, out long block, out int txIndex, out int logIndex)
    {
        block = 0;
        txIndex = 0;
        logIndex = 0;

        int Col(string n) => Array.FindIndex(cols, c => string.Equals(c, n, StringComparison.OrdinalIgnoreCase));
        var ib = Col("blockNumber");
        var itx = Col("transactionIndex");
        var il = Col("logIndex");

        if (ib < 0 || itx < 0 || il < 0) return false;
        if (ib >= row.Length || itx >= row.Length || il >= row.Length) return false;

        try
        {
            block = ToInt64(row[ib]);
            txIndex = ToInt32(row[itx]);
            logIndex = ToInt32(row[il]);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int ToInt32(object? v)
    {
        if (v is null) throw new InvalidCastException("Expected int32 but value is null");
        if (v is int i) return i;
        if (v is long l)
        {
            if (l < int.MinValue || l > int.MaxValue) throw new OverflowException("Int64 out of Int32 range");
            return (int)l;
        }

        if (v is JsonElement je)
        {
            switch (je.ValueKind)
            {
                case JsonValueKind.Number:
                    if (je.TryGetInt32(out var i32)) return i32;
                    if (je.TryGetInt64(out var i64))
                    {
                        if (i64 < int.MinValue || i64 > int.MaxValue)
                            throw new OverflowException("JSON number out of Int32 range");
                        return (int)i64;
                    }

                    break;
                case JsonValueKind.String:
                    if (int.TryParse(je.GetString(), out var p)) return p;
                    if (long.TryParse(je.GetString(), out var l2))
                    {
                        if (l2 < int.MinValue || l2 > int.MaxValue)
                            throw new OverflowException("Parsed string out of Int32 range");
                        return (int)l2;
                    }

                    break;
            }
        }

        if (v is string s)
        {
            if (int.TryParse(s, out var p)) return p;
            if (long.TryParse(s, out var l2))
            {
                if (l2 < int.MinValue || l2 > int.MaxValue)
                    throw new OverflowException("Parsed string out of Int32 range");
                return (int)l2;
            }
        }

        return Convert.ToInt32(v);
    }

    private static long ToInt64(object? v)
    {
        if (v is null) throw new InvalidCastException("Expected int64 but value is null");
        if (v is long l) return l;
        if (v is int i) return (long)i;
        if (v is JsonElement je)
        {
            switch (je.ValueKind)
            {
                case JsonValueKind.Number:
                    if (je.TryGetInt64(out var i64)) return i64;
                    if (je.TryGetInt32(out var i32)) return i32;
                    break;
                case JsonValueKind.String:
                    if (long.TryParse(je.GetString(), out var p)) return p;
                    break;
            }
        }

        if (v is string s && long.TryParse(s, out var p2)) return p2;
        return Convert.ToInt64(v);
    }

    private static string ToStringStrict(object? v)
    {
        var s = ToStringOrNull(v);
        if (string.IsNullOrEmpty(s)) throw new InvalidCastException("Expected non-empty string value");
        return s;
    }

    private static string? ToStringOrNull(object? v)
    {
        if (v is null) return null;
        if (v is string s) return s;
        if (v is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.String) return je.GetString();
        }

        return v.ToString();
    }

    private static BigInteger? ParseUintToBigInteger(object? cell)
    {
        if (cell is null) return null;
        if (cell is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.String)
            {
                var str = je.GetString();
                return ParseUintToBigInteger(str);
            }

            if (je.ValueKind == JsonValueKind.Number)
            {
                if (je.TryGetInt64(out var i64)) return new BigInteger(i64);
                if (je.TryGetInt32(out var i32)) return new BigInteger(i32);
            }

            return null;
        }

        if (cell is string s)
        {
            // string could be big integer in base-10
            if (BigInteger.TryParse(s, out var bi)) return bi;
            // or hex string
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var bytes = Nethereum.Hex.HexConvertors.Extensions.HexByteConvertorExtensions.HexToByteArray(s);
                    var bi2 = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
                    return bi2;
                }
                catch
                {
                    return null;
                }
            }
        }

        if (cell is long l) return new BigInteger(l);
        if (cell is int i) return new BigInteger(i);
        return null;
    }

    /// <summary>
    /// Resolve a CRC v2 ERC1155 token id to its avatar address. Circles uses
    /// toTokenId(avatar) = uint256(uint160(avatar)), so the avatar is the low 160 bits,
    /// rendered as 0x + 40 lowercase hex.
    /// </summary>
    public static string ToAvatarAddress(BigInteger tokenId)
    {
        if (tokenId.Sign < 0) tokenId = -tokenId; // token ids are unsigned; be defensive
        BigInteger mask = (BigInteger.One << 160) - 1;
        BigInteger low = tokenId & mask;

        byte[] be = low.ToByteArray(isUnsigned: true, isBigEndian: true);
        var addr = new byte[20];
        int copy = Math.Min(be.Length, 20);
        Array.Copy(be, be.Length - copy, addr, 20 - copy, copy);
        return "0x" + Convert.ToHexString(addr).ToLowerInvariant();
    }

    /// <summary>
    /// Canonicalize an address for comparison: trimmed, lowercased, 0x-prefixed. Ensures a
    /// checksummed or un-prefixed trustReceiver from the indexer still matches ToAvatarAddress output.
    /// </summary>
    internal static string NormalizeAvatar(string addr)
    {
        var s = addr.Trim().ToLowerInvariant();
        if (!s.StartsWith("0x", StringComparison.Ordinal)) s = "0x" + s;
        return s;
    }

    /// <summary>
    /// Reduce CrcV2_PaymentGateway.TrustUpdated rows for a single gateway into the set of avatar
    /// addresses the gateway currently trusts. The latest row per trustReceiver wins — selected by
    /// explicit (blockNumber, transactionIndex, logIndex) comparison rather than relying on input
    /// ordering (mirrors the core-app reference reducer) — and a receiver is active when its expiry
    /// (unix seconds) is strictly in the future relative to nowUnix.
    /// </summary>
    internal static HashSet<string> ParseActiveTrustSet(string[] cols, object[][] rows, long nowUnix)
    {
        var set = new HashSet<string>();
        if (rows.Length == 0) return set;

        int Col(string n) => Array.FindIndex(cols, c => string.Equals(c, n, StringComparison.OrdinalIgnoreCase));
        var itr = Col("trustReceiver");
        var iexp = Col("expiry");
        if (itr < 0 || iexp < 0) return set;
        var ib = Col("blockNumber");
        var itx = Col("transactionIndex");
        var il = Col("logIndex");

        long Pos(object[] row, int idx)
        {
            if (idx < 0 || idx >= row.Length) return 0;
            try { return ToInt64(row[idx]); }
            catch { return 0; }
        }

        var latest = new Dictionary<string, (BigInteger Expiry, long Block, long Tx, long Log)>();
        foreach (var row in rows)
        {
            if (itr >= row.Length || iexp >= row.Length) continue;
            var receiver = ToStringOrNull(row[itr]);
            if (string.IsNullOrWhiteSpace(receiver)) continue;
            receiver = NormalizeAvatar(receiver);

            var expiry = ParseUintToBigInteger(row[iexp]) ?? BigInteger.Zero;
            long b = Pos(row, ib), t = Pos(row, itx), l = Pos(row, il);

            if (!latest.TryGetValue(receiver, out var prev)
                || b > prev.Block
                || (b == prev.Block && (t > prev.Tx || (t == prev.Tx && l > prev.Log))))
            {
                latest[receiver] = (expiry, b, t, l);
            }
        }

        foreach (var kv in latest)
        {
            if (kv.Value.Expiry > nowUnix) set.Add(kv.Key);
        }

        return set;
    }

    private static string? TryDecodePaymentRef(object? cell)
    {
        if (cell is null) return null;
        if (cell is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.String)
            {
                return TryDecodePaymentRef(je.GetString());
            }

            return null;
        }

        if (cell is string s)
        {
            try
            {
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    var bytes = Nethereum.Hex.HexConvertors.Extensions.HexByteConvertorExtensions.HexToByteArray(s);
                    return Encoding.UTF8.GetString(bytes);
                }

                // might be base64
                var b64 = Convert.FromBase64String(s);
                return Encoding.UTF8.GetString(b64);
            }
            catch
            {
                return null;
            }
        }

        if (cell is byte[] b) return Encoding.UTF8.GetString(b);
        return null;
    }

    public static string? NormalizePaymentReference(string? raw)
    {
        bool hasRaw = !string.IsNullOrWhiteSpace(raw);
        if (!hasRaw)
        {
            return null;
        }

        string trimmed = raw!.Trim();

        bool containsNul = trimmed.IndexOf('\0') >= 0;
        if (containsNul)
        {
            return null;
        }

        const int MaxLen = 256;
        bool tooLong = trimmed.Length > MaxLen;
        if (tooLong)
        {
            return null;
        }

        // --- Backwards compatibility check for pay_... hex references ---
        // If it looks like a standard pay_ reference (36 chars, starts with pay_),
        // we apply the old normalization (uppercase hex) so existing tests pass
        // and we keep a consistent format for those.
        if (trimmed.StartsWith("pay_", StringComparison.OrdinalIgnoreCase))
        {
            if (trimmed.Length != 36)
            {
                return null;
            }

            string hexPart = trimmed.Substring(4);
            bool isHex = true;
            foreach (char c in hexPart)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                {
                    isHex = false;
                    break;
                }
            }

            if (isHex)
            {
                return "pay_" + hexPart.ToUpperInvariant();
            }

            return null;
        }

        return trimmed;
    }

    // --- Cursor persistence ---
    private async Task EnsureCursorTableAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_pgConn);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"CREATE TABLE IF NOT EXISTS payments_cursors (
  chain_id bigint PRIMARY KEY,
  last_block_number bigint NOT NULL,
  last_transaction_index integer NOT NULL,
  last_log_index integer NOT NULL
);";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<(long block, int txi, int log)> LoadCursorAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_pgConn);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT last_block_number, last_transaction_index, last_log_index FROM payments_cursors WHERE chain_id=@c";
        cmd.Parameters.AddWithValue("@c", _chainId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (await r.ReadAsync(ct))
        {
            return (r.GetInt64(0), r.GetInt32(1), r.GetInt32(2));
        }

        return (-1, -1, -1); // start from genesis if not set
    }

    private async Task SaveCursorAsync(long block, int txi, int log, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_pgConn);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            @"INSERT INTO payments_cursors (chain_id, last_block_number, last_transaction_index, last_log_index)
VALUES (@c, @b, @t, @l)
ON CONFLICT (chain_id) DO UPDATE SET last_block_number=@b, last_transaction_index=@t, last_log_index=@l";
        cmd.Parameters.AddWithValue("@c", _chainId);
        cmd.Parameters.AddWithValue("@b", block);
        cmd.Parameters.AddWithValue("@t", txi);
        cmd.Parameters.AddWithValue("@l", log);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
