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

/// <summary>
/// A gateway's reduced trust state. <see cref="All"/> reduces every TrustUpdated row (legacy
/// behavior); <see cref="FactoryAnchored"/> reduces only rows emitted by a canonical gateway
/// factory — null when no factory is configured OR the anchored set is temporarily unavailable
/// (shadow-mode degradation). Forged rows from other emitters are absent from the anchored set
/// entirely (they can neither grant nor revoke trust).
/// </summary>
internal sealed record GatewayTrustSets(HashSet<string> All, HashSet<string>? FactoryAnchored);

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
    // gateway (lowercase) -> (trust sets, fetchedAt). Short-TTL cache so we don't re-query the
    // gateway's on-chain trust list on every transfer.
    private readonly Dictionary<string, (GatewayTrustSets Sets, DateTimeOffset FetchedAt)> _trustCache = new();

    // Canonical gateway factory addresses (lowercase). When set, TrustUpdated rows are additionally
    // reduced to a factory-anchored set (rows emitted by a canonical factory only); when enforced,
    // ONLY that set decides eligibility — an attacker-deployed gateway self-trusting a token emits
    // rows under its own address, which simply don't count. CSV so a factory upgrade (new address)
    // can be expressed without a flag-day. Null = feature off (legacy behavior).
    private readonly IReadOnlyCollection<string>? _gatewayFactories;
    private readonly bool _factoryEnforced;
    private readonly IOrderProcessingTraceSink _trace;

    // Leader election: with multiple market-api nodes sharing one DB, only the lease holder runs the
    // poll/settle/reconcile work so the instances don't race on the same payment rows. Lease-based
    // (not session advisory locks) because the DB is reached via pgbouncer transaction pooling, where
    // session-scoped locks are unreliable. A dead leader's lease expires and a peer takes over.
    private const string PollerName = "payments";
    private readonly bool _leaderElection;
    private readonly string _instanceId;
    private readonly int _leaseSeconds;
    private bool _wasLeader;

    // Settlement reconciliation: periodically re-drive orders that are unpaid despite already holding
    // enough eligible (gateway-trusted) transfer value — self-heals the "payment observed before its
    // order existed" race that the one-shot observe-time match would otherwise strand. 0 disables.
    private readonly int _reconcileSeconds;
    private DateTimeOffset _lastReconcileAt = DateTimeOffset.MinValue;

    // Fulfillment reconciliation: periodically re-drive orders that were paid but never fulfilled
    // (self-heals a missed one-shot fulfillment). Runs in the same leader-elected loop so it is a
    // cross-node singleton. 0 disables the cadence entirely; the reconciler itself defaults to shadow
    // mode (detect + meter, no re-drive) until FULFILLMENT_RECONCILE_ENABLED=true.
    private readonly FulfillmentReconciler _fulfillmentReconciler;
    private readonly int _fulfillmentReconcileSeconds;
    private DateTimeOffset _lastFulfillmentReconcileAt = DateTimeOffset.MinValue;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public CirclesPaymentsPoller(
        ILogger<CirclesPaymentsPoller> log,
        IHttpClientFactory hcf,
        IOrderPaymentFlow paymentFlow,
        IPaymentStore payments,
        FulfillmentReconciler fulfillmentReconciler,
        IOrderProcessingTraceSink trace)
    {
        _log = log;
        _hcf = hcf;
        _fulfillmentReconciler = fulfillmentReconciler ?? throw new ArgumentNullException(nameof(fulfillmentReconciler));
        _paymentFlow = paymentFlow ?? throw new ArgumentNullException(nameof(paymentFlow));
        _payments = payments ?? throw new ArgumentNullException(nameof(payments));
        _trace = trace ?? throw new ArgumentNullException(nameof(trace));
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

        // Leader election is on by default; set PAYMENTS_LEADER_ELECTION=false for single-instance/dev.
        _leaderElection = !string.Equals(Environment.GetEnvironmentVariable("PAYMENTS_LEADER_ELECTION"), "false",
            StringComparison.OrdinalIgnoreCase);
        _instanceId = $"{Environment.MachineName}:{Guid.NewGuid():N}";
        _leaseSeconds = ComputeLeaseSeconds((int)_interval.TotalSeconds,
            Environment.GetEnvironmentVariable("PAYMENTS_LEASE_SECONDS"));
        // Reconciliation cadence (seconds); 0 disables. Defaults to 60s.
        _reconcileSeconds = int.TryParse(Environment.GetEnvironmentVariable("PAYMENTS_RECONCILE_SECONDS"), out var rs)
            ? Math.Max(0, rs)
            : 60;

        // Fulfillment reconciliation cadence (seconds); 0 disables the pass. Defaults to 0 (off) so the
        // feature ships dark: it is turned on explicitly (shadow first) via env after deploy.
        _fulfillmentReconcileSeconds = int.TryParse(Environment.GetEnvironmentVariable("FULFILLMENT_RECONCILE_SECONDS"), out var frs)
            ? Math.Max(0, frs)
            : 0;

        // Factory-anchored gateway enforcement. PAYMENT_GATEWAY_FACTORY names the canonical
        // factory contract(s) (CSV); gateways are created dynamically per seller, so a static
        // allowlist is an operational trap — factory provenance is self-maintaining. Anchoring
        // happens on the TrustUpdated EMITTER (verified: every legitimate trust row is emitted by
        // the factory), NOT on gateway.factory() (a hostile contract can return any address).
        var factoryCsv = Environment.GetEnvironmentVariable("PAYMENT_GATEWAY_FACTORY");
        if (!string.IsNullOrWhiteSpace(factoryCsv)) // empty string = templated-unset, feature off
        {
            var factories = new HashSet<string>(factoryCsv
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(a => a.ToLowerInvariant()));
            foreach (var f in factories)
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(f, "^0x[0-9a-f]{40}$"))
                {
                    throw new InvalidOperationException(
                        $"PAYMENT_GATEWAY_FACTORY contains an invalid address: '{f}'");
                }
            }
            _gatewayFactories = factories;
        }

        _factoryEnforced = string.Equals(
            Environment.GetEnvironmentVariable("PAYMENT_GATEWAY_FACTORY_ENFORCED"), "true",
            StringComparison.OrdinalIgnoreCase);
        if (_factoryEnforced && _gatewayFactories is null)
        {
            throw new InvalidOperationException(
                "PAYMENT_GATEWAY_FACTORY_ENFORCED=true requires PAYMENT_GATEWAY_FACTORY. " +
                "Without the canonical factory address there is no anchored trust set to enforce.");
        }
        if (_gatewayFactories is not null)
        {
            if (_factoryEnforced)
            {
                _log.LogWarning(
                    "Gateway factory anchoring ENFORCED (factories: {Factories}): only trust rows " +
                    "emitted by the canonical factory decide token eligibility.",
                    string.Join(",", _gatewayFactories));
            }
            else
            {
                _log.LogInformation(
                    "Gateway factory anchoring in SHADOW mode (factories: {Factories}): divergence " +
                    "between the legacy and factory-anchored trust verdicts is logged and counted but " +
                    "does not affect settlement. Set PAYMENT_GATEWAY_FACTORY_ENFORCED=true after a clean soak.",
                    string.Join(",", _gatewayFactories));
            }
        }

        bool allowListEmpty = _gatewayAllowList is null || _gatewayAllowList.Count == 0;
        if (!allowListEmpty && _gatewayFactories is not null)
        {
            // The static allowlist drops non-listed gateways at INGESTION with cursor advance —
            // unrecoverable, and it overrides the self-maintaining factory anchor for new sellers.
            _log.LogWarning(
                "Both PAYMENT_GATEWAYS and PAYMENT_GATEWAY_FACTORY are set. The static allowlist drops " +
                "payments from unlisted gateways irrecoverably at ingestion — a factory-created gateway " +
                "missing from the list loses payments even though factory anchoring would accept it. " +
                "Prefer factory anchoring alone once enforced.");
        }
        if (allowListEmpty && !_factoryEnforced)
        {
            // Token-trust is always enforced per gateway, but until factory anchoring is ENFORCED
            // a payment routed through an attacker-controlled gateway that self-trusts a token is
            // still credited. Shadow mode observes; it does not close the hole.
            _log.LogWarning(
                "Gateway authorization is not enforced ({State}): payment events from ANY gateway are " +
                "processed and an attacker-controlled gateway that self-trusts a token would be credited. " +
                "Set PAYMENT_GATEWAY_FACTORY and, after a clean shadow soak, PAYMENT_GATEWAY_FACTORY_ENFORCED=true.",
                _gatewayFactories is null ? "no factory configured" : "factory in shadow mode");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await EnsureCursorTableAsync(stoppingToken);
            if (_leaderElection)
            {
                await EnsureLeaderTableAsync(stoppingToken);
                _log.LogInformation(
                    "Payments poller leader election enabled (instance {Instance}, lease {Lease}s)",
                    _instanceId, _leaseSeconds);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to ensure payments poller tables");
            throw;
        }

        var http = _hcf.CreateClient();
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                bool isLeader = !_leaderElection || await TryAcquireLeadershipAsync(stoppingToken);
                if (_leaderElection && isLeader != _wasLeader)
                {
                    // Log only on transitions so "who is leading" is visible at Info without per-cycle noise.
                    _log.LogInformation(isLeader
                        ? "Payments poller acquired leadership (instance {Instance})"
                        : "Payments poller lost leadership (instance {Instance}); another instance is leading",
                        _instanceId);
                    _wasLeader = isLeader;
                }

                if (isLeader)
                {
                    await TickAsync(http, stoppingToken);
                    await MaybeReconcileAsync(stoppingToken);
                    await MaybeReconcileFulfillmentAsync(stoppingToken);
                }
                else
                {
                    _log.LogDebug("Payments poller is a follower this cycle; another instance holds the lease");
                }
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

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Release our lease on graceful shutdown (e.g. rolling deploy drain) so a peer takes over
        // immediately instead of waiting for the lease to expire. Use an independent short timeout, not
        // the host shutdown token (which is typically already cancelling), so the release actually runs.
        if (_leaderElection)
        {
            try
            {
                using var releaseCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await ReleaseLeadershipAsync(releaseCts.Token);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Failed to release payments poller lease on shutdown; a peer will take over after lease expiry");
            }
        }

        await base.StopAsync(cancellationToken);
    }

    private async Task MaybeReconcileAsync(CancellationToken ct)
    {
        if (_reconcileSeconds <= 0) return;
        if (DateTimeOffset.UtcNow - _lastReconcileAt < TimeSpan.FromSeconds(_reconcileSeconds)) return;
        // Stamp before running so a slow or failing pass is rate-limited rather than hot-looping.
        _lastReconcileAt = DateTimeOffset.UtcNow;

        // Renew/confirm leadership immediately before this heavier pass: a tick that consumed most of
        // the lease window must not let us reconcile after a peer has already taken over.
        if (_leaderElection && !await TryAcquireLeadershipAsync(ct)) return;

        try
        {
            await ReconcileStrandedOrdersAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Settlement reconciliation pass failed");
        }
    }

    private async Task MaybeReconcileFulfillmentAsync(CancellationToken ct)
    {
        if (_fulfillmentReconcileSeconds <= 0) return;
        if (DateTimeOffset.UtcNow - _lastFulfillmentReconcileAt < TimeSpan.FromSeconds(_fulfillmentReconcileSeconds)) return;
        // Stamp before running so a slow or failing pass is rate-limited rather than hot-looping.
        _lastFulfillmentReconcileAt = DateTimeOffset.UtcNow;

        // Renew/confirm leadership immediately before this heavier pass: a tick that consumed most of
        // the lease window must not let us re-drive fulfillment after a peer has already taken over.
        if (_leaderElection && !await TryAcquireLeadershipAsync(ct)) return;

        try
        {
            await _fulfillmentReconciler.ReconcileOnceAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Fulfillment reconciliation pass failed");
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
                    var trustSets = await GetGatewayTrustSetsAsync(http, transfer.GatewayAddress, ct);
                    transfer = transfer with { Eligible = ResolveEligibility(trustSets, transfer) };

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

            var trustSets = await GetGatewayTrustSetsAsync(http, t.GatewayAddress, ct);
            // Reuse the single eligibility decision; null = still undetermined (no avatar or trust
            // data unavailable) → leave it and retry next tick.
            var verdict = ResolveEligibility(trustSets, t);
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
    /// Active trusted-avatar sets for a gateway, cached for _trustCacheTtl. Returns null when the
    /// sets cannot be determined (RPC error and no cached value), so the caller leaves the transfer
    /// undetermined and retries later rather than wrongly rejecting it.
    /// </summary>
    private async Task<GatewayTrustSets?> GetGatewayTrustSetsAsync(HttpClient http, string gateway, CancellationToken ct)
    {
        gateway = gateway.ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        if (_trustCache.TryGetValue(gateway, out var cached) && (now - cached.FetchedAt) < _trustCacheTtl)
        {
            return cached.Sets;
        }

        try
        {
            var sets = await FetchActiveTrustSetsAsync(http, gateway, now.ToUnixTimeSeconds(), ct);
            _trustCache[gateway] = (sets, now);
            return sets;
        }
        catch (Exception ex)
        {
            Metrics.MarketplaceMetrics.GatewayTrustFetchFailures.Inc();
            _log.LogWarning(ex, "Failed to fetch gateway trust set for {Gateway}", gateway);
            // Fall back to stale cache if we have one; else undetermined (null).
            return _trustCache.TryGetValue(gateway, out var stale) ? stale.Sets : null;
        }
    }

    /// <summary>
    /// Resolves a transfer's eligibility and surfaces divergence between the legacy (all-emitters)
    /// and factory-anchored verdicts: in shadow mode divergence is the flip gate; in enforced mode
    /// it distinguishes "blocked forged trust / broken legit gateway" from ordinary untrusted-token
    /// rejections. Divergences are persisted as a trace event so the forensic record outlives log
    /// retention (the flip decision depends on it).
    /// </summary>
    private bool? ResolveEligibility(GatewayTrustSets? sets, PaymentTransferRecord transfer)
    {
        var (verdict, diverged) = DecideFactoryAwareEligibility(sets, transfer.TokenAvatar, _gatewayFactories is not null, _factoryEnforced);
        if (diverged)
        {
            string mode = _factoryEnforced ? "enforced" : "shadow";
            var legacy = DecideEligibility(sets?.All, transfer.TokenAvatar);
            var anchored = DecideEligibility(sets?.FactoryAnchored, transfer.TokenAvatar);
            Metrics.MarketplaceMetrics.PaymentsFactoryDivergence.WithLabels(mode).Inc();
            _log.LogWarning(
                "Factory-anchoring divergence ({Mode}) for gateway {Gateway}, token avatar {Avatar}, tx {Tx}, ref {Ref}: " +
                "legacy verdict {Legacy}, factory-anchored verdict {Anchored}",
                mode, transfer.GatewayAddress, transfer.TokenAvatar, transfer.TxHash, transfer.PaymentReference,
                legacy, anchored);
            _trace.Emit(new OrderProcessingTraceEvent(
                OccurredAt: DateTimeOffset.UtcNow,
                Stage: "payment_factory_divergence",
                Status: "warn",
                OrderId: null,
                PaymentReference: transfer.PaymentReference,
                ChainId: transfer.ChainId,
                SellerAddress: null,
                BuyerAddress: transfer.PayerAddress,
                ReasonCode: mode,
                Message: $"gateway {transfer.GatewayAddress} token {transfer.TokenAvatar} tx {transfer.TxHash}: legacy={legacy} anchored={anchored}",
                Details: null));
        }
        return verdict;
    }

    /// <summary>
    /// Verdict selection across the two trust sets. Enforced → the factory-anchored set decides
    /// (divergence still reported, for observability only). Shadow (factory configured, not
    /// enforced) → the legacy set decides; a differing anchored verdict is reported. An
    /// unavailable anchored set (null while configured, e.g. shadow-mode degradation) is
    /// "cannot compare", never a divergence. Factory unset → legacy set only.
    /// </summary>
    internal static (bool? Verdict, bool Diverged) DecideFactoryAwareEligibility(
        GatewayTrustSets? sets, string? tokenAvatar, bool factoryConfigured, bool enforced)
    {
        if (enforced)
        {
            var anchoredVerdict = DecideEligibility(sets?.FactoryAnchored, tokenAvatar);
            var legacyVerdict = DecideEligibility(sets?.All, tokenAvatar);
            return (anchoredVerdict, sets is not null && anchoredVerdict != legacyVerdict);
        }

        var legacy = DecideEligibility(sets?.All, tokenAvatar);
        if (factoryConfigured && sets?.FactoryAnchored is not null)
        {
            var anchored = DecideEligibility(sets.FactoryAnchored, tokenAvatar);
            return (legacy, anchored != legacy);
        }
        return (legacy, false);
    }

    private const int TrustQueryLimit = 1000;

    private async Task<GatewayTrustSets> FetchActiveTrustSetsAsync(HttpClient http, string gateway, long nowUnix,
        CancellationToken ct)
    {
        // Legacy (all-emitters) set: unfiltered query, exactly as before this feature.
        var allResult = await QueryTrustRowsAsync(http, gateway, emitter: null, ct);
        var all = ParseActiveTrustSet(allResult.Columns, allResult.Rows, nowUnix);

        if (_gatewayFactories is null)
        {
            return new GatewayTrustSets(all, null);
        }

        // Anchored set. With a single factory the emitter predicate runs SERVER-SIDE so forged
        // rows cannot consume the row budget (flooding a victim gateway past the query limit
        // would otherwise push the newest factory rows out of the window). The client-side
        // filter in ParseActiveTrustSet stays on as defense-in-depth (a server that silently
        // ignored the predicate would otherwise hand us unanchored rows). With multiple
        // factories (migration window) the filter is client-side only — the truncation guard
        // below still bounds the exposure.
        string? serverEmitter = _gatewayFactories.Count == 1 ? _gatewayFactories.First() : null;
        try
        {
            var anchoredResult = serverEmitter is not null
                ? await QueryTrustRowsAsync(http, gateway, serverEmitter, ct)
                : allResult;

            if (anchoredResult.Rows.Length > 0 &&
                Array.FindIndex(anchoredResult.Columns, c => string.Equals(c, "emitter", StringComparison.OrdinalIgnoreCase)) < 0)
            {
                // The anchored set is meaningless without emitter attribution — schema drift, not
                // "gateway trusts nothing".
                throw new InvalidOperationException(
                    $"circles_query TrustUpdated missing emitter column for gateway {gateway} (required for factory anchoring)");
            }

            var anchored = ParseActiveTrustSet(anchoredResult.Columns, anchoredResult.Rows, nowUnix, _gatewayFactories);
            return new GatewayTrustSets(all, anchored);
        }
        catch (Exception ex) when (!_factoryEnforced && ex is not OperationCanceledException)
        {
            // Shadow must never affect settlement: degrade to "anchored unavailable" (no
            // divergence is reported for it) instead of failing the whole fetch into the
            // undetermined path. Enforced mode propagates — there, an unavailable anchored set
            // must leave transfers undetermined rather than fall back to the legacy verdict.
            _log.LogError(ex,
                "Factory-anchored trust set unavailable for gateway {Gateway} (shadow mode; legacy verdict unaffected)",
                gateway);
            return new GatewayTrustSets(all, null);
        }
    }

    private async Task<CirclesQueryResult> QueryTrustRowsAsync(HttpClient http, string gateway, string? emitter,
        CancellationToken ct)
    {
        var filter = new List<object>
        {
            new { Type = "FilterPredicate", FilterType = "Equals", Column = "gateway", Value = gateway }
        };
        if (emitter is not null)
        {
            filter.Add(new { Type = "FilterPredicate", FilterType = "Equals", Column = "emitter", Value = emitter });
        }

        var reqModel = new CirclesQueryRequest
        {
            Namespace = "CrcV2_PaymentGateway",
            Table = "TrustUpdated",
            Columns = new(),
            Filter = filter,
            Order = new()
            {
                new OrderSpec { Column = "blockNumber", SortOrder = "ASC" },
                new OrderSpec { Column = "transactionIndex", SortOrder = "ASC" },
                new OrderSpec { Column = "logIndex", SortOrder = "ASC" }
            },
            Limit = TrustQueryLimit
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

        // A full page means the window may have been truncated: reducing a partial row set could
        // both hide newer revocations (fail-open) and hide newer grants (fail-closed). Refuse and
        // leave transfers undetermined rather than decide on incomplete data.
        if (result.Rows.Length >= TrustQueryLimit)
        {
            throw new InvalidOperationException(
                $"circles_query TrustUpdated returned {result.Rows.Length} rows (query limit) for gateway {gateway}" +
                (emitter is null ? "" : $" emitter {emitter}") +
                " — trust list truncated, refusing to reduce a partial window");
        }

        return result;
    }

    /// <summary>
    /// Lease duration (seconds) for leader election. The lease must comfortably outlive a full work
    /// cycle (a poll tick plus the periodic reconcile batch), not just the poll sleep, so a slow cycle
    /// cannot let the lease lapse mid-work. With no override the floor is max(30, 6x poll interval). An
    /// explicit PAYMENTS_LEASE_SECONDS is honoured but still floored to one second past the poll interval
    /// so it can never be shorter than a single cycle; the leader additionally renews the lease right
    /// before the heavier reconcile pass.
    /// </summary>
    internal static int ComputeLeaseSeconds(int pollSeconds, string? leaseEnv)
    {
        var poll = Math.Max(1, pollSeconds);
        var floor = poll + 1;
        var defaultLease = Math.Max(30, poll * 6);
        return int.TryParse(leaseEnv, out var ls) ? Math.Max(floor, ls) : defaultLease;
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
    ///
    /// With <paramref name="emitterFilter"/> set, rows emitted by any address OUTSIDE the filter
    /// set are discarded BEFORE the latest-wins reduction — a forged row can neither add trust
    /// nor, by being newer, revoke trust the canonical factory established.
    /// NOTE: this fail-safe (missing/unattributable emitter → row discarded, possibly yielding an
    /// empty all-rejecting set) is only correct because the production fetch path throws upstream
    /// on emitter-column schema drift; direct callers must apply the same guard.
    /// </summary>
    internal static HashSet<string> ParseActiveTrustSet(string[] cols, object[][] rows, long nowUnix,
        IReadOnlyCollection<string>? emitterFilter = null)
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
        var iem = Col("emitter");
        if (emitterFilter is not null && iem < 0) return set; // cannot attribute rows — anchored set empty (fail-safe)

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

            if (emitterFilter is not null)
            {
                var emitter = iem < row.Length ? ToStringOrNull(row[iem]) : null;
                if (emitter is null || !emitterFilter.Contains(NormalizeAvatar(emitter)))
                {
                    continue;
                }
            }

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

    private async Task EnsureLeaderTableAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_pgConn);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"CREATE TABLE IF NOT EXISTS poller_leader (
  poller_name      text        PRIMARY KEY,
  instance_id      text        NOT NULL,
  lease_expires_at timestamptz NOT NULL
);";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Lease-based leader election. Returns true iff this instance holds the poller lease for the next
    /// window. A single atomic UPSERT, so it is correct under pgbouncer transaction pooling (where
    /// session-scoped advisory locks are unreliable). The conflicting UPDATE only fires when the current
    /// lease has expired or is already ours, so a returned row unambiguously means we are the leader.
    /// </summary>
    private async Task<bool> TryAcquireLeadershipAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_pgConn);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO poller_leader (poller_name, instance_id, lease_expires_at)
VALUES (@name, @me, now() + make_interval(secs => @lease))
ON CONFLICT (poller_name) DO UPDATE
  SET instance_id      = EXCLUDED.instance_id,
      lease_expires_at = EXCLUDED.lease_expires_at
  WHERE poller_leader.lease_expires_at < now()
     OR poller_leader.instance_id = EXCLUDED.instance_id
RETURNING instance_id;";
        cmd.Parameters.AddWithValue("@name", PollerName);
        cmd.Parameters.AddWithValue("@me", _instanceId);
        cmd.Parameters.AddWithValue("@lease", (double)_leaseSeconds);
        var result = await cmd.ExecuteScalarAsync(ct);
        // A returned instance_id means we inserted, renewed, or took over an expired lease. Null means a
        // valid lease is held by another instance — we are a follower this cycle.
        return result is string;
    }

    private async Task ReleaseLeadershipAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_pgConn);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        // Expire only our own lease; no-op if a peer already owns it.
        cmd.CommandText =
            "UPDATE poller_leader SET lease_expires_at = now() WHERE poller_name=@name AND instance_id=@me;";
        cmd.Parameters.AddWithValue("@name", PollerName);
        cmd.Parameters.AddWithValue("@me", _instanceId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Defense-in-depth: re-drive settlement for orders that are still unpaid (paid_at IS NULL) yet
    /// already hold enough eligible (gateway-trusted) transfer value to cover the order total. This
    /// self-heals the "payment observed before its order existed" race, where the one-shot observe-time
    /// match missed and nothing else retries. Re-driving through HandleObservedTransferAsync re-aggregates
    /// and marks the order paid; already-paid orders are excluded by the paid_at IS NULL filter, and the
    /// transfer upsert is idempotent.
    /// </summary>
    private async Task ReconcileStrandedOrdersAsync(CancellationToken ct)
    {
        var references = new List<string>();
        await using (var conn = new NpgsqlConnection(_pgConn))
        {
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            // Only orders with an explicit price that the eligible transfer total meets are re-driven.
            // Price-less orders (expected NULL) are deliberately NOT reconciled here: the observe-time
            // path auto-settles them on any transfer, a behavior already flagged for review as a bypass,
            // so reconciliation does not propagate it. The wei threshold matches TryMarkPaidByReference.
            cmd.CommandText = @"
SELECT o.payment_reference
FROM orders o
JOIN (
  SELECT chain_id, payment_reference,
         SUM(amount_wei) FILTER (WHERE eligible IS TRUE) AS eligible_sum
  FROM payment_transfers
  WHERE chain_id = @chain
  GROUP BY chain_id, payment_reference
) t ON t.payment_reference = o.payment_reference AND t.chain_id = @chain
WHERE o.paid_at IS NULL
  AND o.payment_reference IS NOT NULL
  AND t.eligible_sum IS NOT NULL
  -- Guard the cast: only well-formed numeric prices, so one malformed order_json can't error the pass.
  AND (o.order_json->'totalPaymentDue'->>'price') ~ '^[0-9]+(\.[0-9]+)?$'
  AND t.eligible_sum >= ((o.order_json->'totalPaymentDue'->>'price')::numeric * 1000000000000000000)
LIMIT 200;";
            cmd.Parameters.AddWithValue("@chain", _chainId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (!r.IsDBNull(0)) references.Add(r.GetString(0));
            }
        }

        if (references.Count == 0) return;
        _log.LogWarning(
            "Reconciliation found {Count} unpaid order(s) with sufficient eligible payment; re-driving settlement",
            references.Count);

        foreach (var reference in references)
        {
            ct.ThrowIfCancellationRequested();
            var eligibleTransfer = _payments.GetTransfersByReference(_chainId, reference)
                .FirstOrDefault(t => t.Eligible == true);
            if (eligibleTransfer is null)
            {
                // The SELECT guarantees an eligible transfer exists for this reference, so a miss here
                // signals real data drift (transfer changed/removed between the two queries) — surface it.
                _log.LogWarning(
                    "Reconciliation: no eligible transfer for reference {Ref} despite a qualifying eligible sum",
                    reference);
                continue;
            }
            // Re-run the standard observe -> aggregate -> settle path; idempotent for the existing row.
            // Re-aggregation re-reads ALL transfers for the reference, so multi-transfer orders still
            // settle on the full total even though we feed it a single eligible transfer. Count the
            // metric only when this actually marked an order paid, so it reflects real settlements.
            if (await _paymentFlow.HandleObservedTransferAsync(eligibleTransfer, ct))
            {
                Metrics.MarketplaceMetrics.PaymentsReconciled.Inc();
            }
        }
    }
}
