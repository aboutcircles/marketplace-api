using System.Text.Json;
using Circles.Market.Api.Cart;
using Circles.Market.Api.Metrics;
using Npgsql;

namespace Circles.Market.Api.Payments;

/// <summary>A paid order that has no successful fulfillment yet (candidate for re-drive).</summary>
public sealed record StrandedOrder(string OrderId, string PaymentReference);

/// <summary>Outcome of a single reconcile pass; returned for logging/tests.</summary>
public sealed record FulfillmentReconcileResult(int Candidates, int Reconciled, int Failed, bool Enabled);

/// <summary>
/// Parameters of a stranded-order candidate query. <see cref="OrderIds"/> (when non-null)
/// restricts candidates to an explicit allowlist — the targeted-recovery mode; and
/// <see cref="SettledAfter"/> (when non-null) excludes anything settled at/before the
/// cutoff — the steady-state mode that fences off the historic backlog. At least one of
/// the two is REQUIRED for an active (re-driving) reconciler: the unscoped candidate set
/// is "no fulfillment outbox row", which is known to include orders that were actually
/// fulfilled before success markers existed — re-driving those double-ships.
///
/// Granularity note: the re-drive fires the lifecycle hooks per PAYMENT REFERENCE, which
/// fan out to every order sharing that reference. The allowlist therefore effectively
/// scopes by an allowlisted order's payment reference, not strictly by order id — vet
/// candidates at reference granularity.
/// </summary>
public sealed record StrandedQuery(
    int GraceMinutes,
    int MaxAttempts,
    int MaxAgeDays,
    int Limit,
    IReadOnlyCollection<string>? OrderIds,
    DateTimeOffset? SettledAfter);

/// <summary>
/// Supplies the set of orders that are paid/finalized but have no successful fulfillment.
/// Abstracted so the reconciler's orchestration is unit-testable without a database (the
/// concrete SQL implementation follows the same "not unit-tested" convention as the poller SQL).
/// </summary>
public interface IStrandedFulfillmentSource
{
    Task<IReadOnlyList<StrandedOrder>> GetStrandedAsync(StrandedQuery query, CancellationToken ct);
}

/// <summary>
/// Postgres-backed stranded-order detection.
///
/// A candidate is an order that:
///   - is paid (has finalized_at or confirmed_at) past the grace window, AND
///   - was paid within the max-age window (backstop below), AND
///   - has NO 'fulfillment' outbox row (the success marker written by the fulfillment hook), AND
///   - is still under the per-order reconcile attempt cap ('fulfillment_reconcile' markers).
///
/// The grace window lets the normal fulfillment path complete before we intervene. The attempt
/// cap is the PRIMARY termination bound for perpetually-failing / route-less orders. The max-age
/// window is a SECONDARY hard backstop: the attempt-cap counter is itself an order_outbox write
/// (OrderStore.AddOutboxItem, which swallows INSERT failures), so if marker writes are the thing
/// failing the cap could never advance — the age cutoff guarantees the order eventually stops being
/// re-selected regardless of whether any marker write ever landed.
/// </summary>
public sealed class PostgresStrandedFulfillmentSource : IStrandedFulfillmentSource
{
    private readonly string _connString;

    public PostgresStrandedFulfillmentSource(string connString)
    {
        _connString = connString ?? throw new ArgumentNullException(nameof(connString));
    }

    public async Task<IReadOnlyList<StrandedOrder>> GetStrandedAsync(StrandedQuery query, CancellationToken ct)
    {
        var result = new List<StrandedOrder>();
        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();

        string scopeClauses = "";
        if (query.OrderIds is not null)
        {
            scopeClauses += "\n  AND o.order_id = ANY(@orderIds)";
            cmd.Parameters.Add(new NpgsqlParameter("@orderIds", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text)
            {
                Value = query.OrderIds.ToArray()
            });
        }
        if (query.SettledAfter is not null)
        {
            scopeClauses += "\n  AND COALESCE(o.finalized_at, o.confirmed_at) > @settledAfter";
            // Npgsql rejects non-zero-offset DateTimeOffset against timestamptz.
            cmd.Parameters.AddWithValue("@settledAfter", query.SettledAfter.Value.ToUniversalTime());
        }

        cmd.CommandText = @"
SELECT o.order_id, o.payment_reference
FROM orders o
WHERE o.payment_reference IS NOT NULL
  AND COALESCE(o.finalized_at, o.confirmed_at) IS NOT NULL
  AND COALESCE(o.finalized_at, o.confirmed_at) < now() - make_interval(mins => @grace)
  AND COALESCE(o.finalized_at, o.confirmed_at) > now() - make_interval(days => @maxAge)" + scopeClauses + @"
  AND NOT EXISTS (
    SELECT 1 FROM order_outbox ob
    WHERE ob.order_id = o.order_id AND ob.source = 'fulfillment'
  )
  AND (
    SELECT count(*) FROM order_outbox ob2
    WHERE ob2.order_id = o.order_id AND ob2.source = 'fulfillment_reconcile'
  ) < @maxAttempts
ORDER BY COALESCE(o.finalized_at, o.confirmed_at) ASC
LIMIT @limit;";
        cmd.Parameters.AddWithValue("@grace", query.GraceMinutes);
        cmd.Parameters.AddWithValue("@maxAttempts", query.MaxAttempts);
        cmd.Parameters.AddWithValue("@maxAge", query.MaxAgeDays);
        cmd.Parameters.AddWithValue("@limit", query.Limit);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            if (r.IsDBNull(0) || r.IsDBNull(1)) continue;
            result.Add(new StrandedOrder(r.GetString(0), r.GetString(1)));
        }

        return result;
    }
}

/// <summary>
/// Detects orders that were paid but never fulfilled and re-drives them through the NORMAL
/// fulfillment path (the same lifecycle hooks the payments poller fires). This recovers orders the
/// one-shot fulfillment missed and self-heals the class going forward.
///
/// Double-shipment safety: the re-drive goes THROUGH the adapter's /fulfill endpoint, so it inherits
/// both idempotency layers — the fulfillment_runs gate (status='ok' -> AlreadyProcessed no-op) and,
/// once a run is acquired, the client_order_ref reuse guard. For post-#47 orders this is airtight.
/// The only residual risk is a pre-#47 order that was actually fulfilled but whose run is not 'ok'
/// (its Odoo order lacks client_order_ref); that backlog is handled by running in shadow mode first
/// and Odoo-vetting the candidate list before enabling. See FULFILLMENT_RECONCILE_ENABLED.
///
/// Shadow mode (FULFILLMENT_RECONCILE_ENABLED=false, the default): detect + log + set the stranded
/// gauge, but fire nothing. Flip to true only after the candidate list is verified safe.
/// </summary>
public sealed class FulfillmentReconciler
{
    private readonly IStrandedFulfillmentSource _source;
    private readonly IOrderLifecycleHooks _hooks;
    private readonly IOrderStore _orders;
    private readonly ILogger<FulfillmentReconciler> _log;

    private readonly bool _enabled;
    private readonly int _graceMinutes;
    private readonly int _maxAttempts;
    private readonly int _maxAgeDays;
    private readonly int _limit;
    private readonly IReadOnlyCollection<string>? _orderIds;
    private readonly DateTimeOffset? _settledAfter;

    public FulfillmentReconciler(
        IStrandedFulfillmentSource source,
        IOrderLifecycleHooks hooks,
        IOrderStore orders,
        ILogger<FulfillmentReconciler> log)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        // Default OFF (shadow). Set FULFILLMENT_RECONCILE_ENABLED=true only after vetting.
        _enabled = string.Equals(
            Environment.GetEnvironmentVariable("FULFILLMENT_RECONCILE_ENABLED"), "true",
            StringComparison.OrdinalIgnoreCase);

        _graceMinutes = ReadPositiveInt("FULFILLMENT_RECONCILE_GRACE_MINUTES", 15);
        _maxAttempts = ReadPositiveInt("FULFILLMENT_RECONCILE_MAX_ATTEMPTS", 5);
        // Wide by default so a one-time backlog recovery reaches old stranded orders; this is a
        // hard-termination backstop, not the primary bound (that is the attempt cap).
        _maxAgeDays = ReadPositiveInt("FULFILLMENT_RECONCILE_MAX_AGE_DAYS", 400);
        _limit = Math.Clamp(ReadPositiveInt("FULFILLMENT_RECONCILE_LIMIT", 100), 1, 1000);

        _orderIds = ReadOrderIds("FULFILLMENT_RECONCILE_ORDER_IDS");
        _settledAfter = ReadTimestamp("FULFILLMENT_RECONCILE_SETTLED_AFTER");

        // An ACTIVE reconciler must be scoped: the unscoped candidate set ("no fulfillment
        // outbox row") is known to include orders that were fulfilled before success markers
        // existed — re-driving those double-ships. Shadow mode may run unscoped (it only logs).
        if (_enabled && _orderIds is null && _settledAfter is null)
        {
            throw new InvalidOperationException(
                "FULFILLMENT_RECONCILE_ENABLED=true requires FULFILLMENT_RECONCILE_ORDER_IDS " +
                "(explicit targeted-recovery allowlist) and/or FULFILLMENT_RECONCILE_SETTLED_AFTER " +
                "(cutoff fencing off the historic backlog). An unscoped active reconciler would " +
                "re-drive already-fulfilled orders that lack success markers and double-ship.");
        }
    }

    private static int ReadPositiveInt(string envName, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(envName);
        return int.TryParse(raw, out var v) && v > 0 ? v : fallback;
    }

    /// <summary>
    /// Comma/whitespace-separated order-id allowlist. Null when the variable is absent or an
    /// empty string (the common templated-unset shape). A non-empty value that parses to zero
    /// ids throws: silently degrading an intended allowlist to "no allowlist" would widen the
    /// scope — e.g. to cutoff-only when SETTLED_AFTER is also set.
    /// </summary>
    private static IReadOnlyCollection<string>? ReadOrderIds(string envName)
    {
        var raw = Environment.GetEnvironmentVariable(envName);
        if (string.IsNullOrEmpty(raw)) return null;
        var ids = raw
            .Split([',', ' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0)
        {
            throw new InvalidOperationException(
                $"{envName} is set but contains no order ids: '{raw}'");
        }
        return ids;
    }

    private static readonly string[] TimestampFormats =
    [
        "O",
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd"
    ];

    /// <summary>
    /// Strict ISO-8601 timestamp; null when unset. A SET-but-unparsable value throws, and
    /// only unambiguous ISO shapes are accepted — lenient parsing would let a day/month-
    /// swapped date through as a valid-looking cutoff months off target, silently widening
    /// the re-drive window. Offset-less values are read as UTC.
    /// </summary>
    private static DateTimeOffset? ReadTimestamp(string envName)
    {
        var raw = Environment.GetEnvironmentVariable(envName);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!DateTimeOffset.TryParseExact(raw, TimestampFormats,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var value))
        {
            throw new InvalidOperationException(
                $"{envName} is set but not an ISO-8601 timestamp (e.g. 2026-07-03T10:00:00Z): '{raw}'");
        }
        return value;
    }

    private string ScopeDescription =>
        (_orderIds, _settledAfter) switch
        {
            (not null, not null) => $"order-id allowlist ({_orderIds.Count}) + settled after {_settledAfter:o}",
            (not null, null) => $"order-id allowlist ({_orderIds.Count})",
            (null, not null) => $"settled after {_settledAfter:o}",
            _ => "UNSCOPED (shadow only)"
        };

    public async Task<FulfillmentReconcileResult> ReconcileOnceAsync(CancellationToken ct)
    {
        var candidates = await _source.GetStrandedAsync(
            new StrandedQuery(_graceMinutes, _maxAttempts, _maxAgeDays, _limit, _orderIds, _settledAfter), ct);

        // Dedupe by payment reference: the hooks fan out to every order sharing the reference, so
        // firing once per reference is sufficient (and avoids double-firing a shared reference).
        var byReference = new Dictionary<string, StrandedOrder>(StringComparer.Ordinal);
        foreach (var c in candidates)
        {
            byReference.TryAdd(c.PaymentReference, c);
        }

        MarketplaceMetrics.FulfillmentStranded.Set(byReference.Count);

        // With an explicit allowlist the operator EXPECTS action — zero matches must be
        // visible, not inferred from the absence of a log line (typo'd ids, already
        // recovered, and filtered-out-by-bounds are otherwise indistinguishable).
        if (_orderIds is not null)
        {
            _log.LogInformation(
                "Fulfillment reconciler allowlist: {AllowlistCount} order id(s) configured, {Matched} candidate(s) matched",
                _orderIds.Count, candidates.Count);
        }

        if (byReference.Count == 0)
        {
            return new FulfillmentReconcileResult(0, 0, 0, _enabled);
        }

        if (!_enabled)
        {
            // Shadow mode: surface what WOULD be re-driven so it can be vetted before enabling.
            _log.LogWarning(
                "Fulfillment reconciler (SHADOW, disabled, scope: {Scope}): {Count} stranded order(s) detected but NOT re-driven. " +
                "Set FULFILLMENT_RECONCILE_ENABLED=true after verifying the candidates. Sample: {Sample}",
                ScopeDescription,
                byReference.Count,
                string.Join(", ", byReference.Values.Take(10).Select(o => o.OrderId)));
            return new FulfillmentReconcileResult(byReference.Count, 0, 0, false);
        }

        _log.LogWarning(
            "Fulfillment reconciler (ACTIVE, scope: {Scope}): re-driving {Count} stranded order(s)",
            ScopeDescription,
            byReference.Count);

        int reconciled = 0;
        int failed = 0;

        foreach (var candidate in byReference.Values)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var now = DateTimeOffset.UtcNow;
                // Re-run the exact normal fulfillment path. Each offer's trigger filter lets only its
                // matching hook reach the adapter; the adapter gate serializes/deduplicates the rest.
                await _hooks.OnConfirmedAsync(candidate.PaymentReference, now, ct);
                await _hooks.OnFinalizedAsync(candidate.PaymentReference, now, ct);

                // A successful re-drive writes a 'fulfillment' outbox row (incl. the adapter's
                // AlreadyProcessed 200 for already-fulfilled orders). Its presence is our success signal.
                // Caveat: that write goes through OrderStore.AddOutboxItem, which swallows INSERT
                // failures — so a genuine fulfillment whose marker write was lost reads here as "still
                // stranded" and gets re-driven next pass. That re-drive is SAFE (the adapter gate returns
                // AlreadyProcessed → no duplicate shipment), and both the attempt cap and the age backstop
                // bound the retries. We accept the occasional miscounted failure rather than fail a served
                // customer's fulfillment on a lost log write.
                bool fulfilled = _orders.GetOutboxItems(candidate.OrderId)
                    .Any(i => string.Equals(i.Source, "fulfillment", StringComparison.Ordinal));

                if (fulfilled)
                {
                    reconciled++;
                    MarketplaceMetrics.FulfillmentReconciled.Inc();
                    _log.LogInformation(
                        "Fulfillment reconciler recovered order {OrderId} (ref {Ref})",
                        candidate.OrderId, candidate.PaymentReference);
                }
                else
                {
                    failed++;
                    MarketplaceMetrics.FulfillmentReconcileFailed.Inc();
                    RecordAttempt(candidate, "still_stranded");
                    _log.LogWarning(
                        "Fulfillment reconciler re-drove order {OrderId} (ref {Ref}) but no fulfillment resulted; " +
                        "recorded attempt (retried until cap)",
                        candidate.OrderId, candidate.PaymentReference);
                }
            }
            catch (Exception ex)
            {
                failed++;
                MarketplaceMetrics.FulfillmentReconcileFailed.Inc();
                RecordAttempt(candidate, "error");
                _log.LogError(ex,
                    "Fulfillment reconciler failed to re-drive order {OrderId} (ref {Ref})",
                    candidate.OrderId, candidate.PaymentReference);
            }
        }

        return new FulfillmentReconcileResult(byReference.Count, reconciled, failed, true);
    }

    // Records a bounded-retry marker so route-less / persistently-failing orders stop being
    // re-selected once the attempt cap is reached (the detection query counts these markers).
    private void RecordAttempt(StrandedOrder candidate, string outcome)
    {
        try
        {
            var marker = JsonSerializer.SerializeToElement(new
            {
                reconcile = "attempt",
                outcome,
                orderId = candidate.OrderId,
                paymentReference = candidate.PaymentReference,
                at = DateTimeOffset.UtcNow
            });
            _orders.AddOutboxItem(candidate.OrderId, "fulfillment_reconcile", marker);
        }
        catch (Exception ex)
        {
            // Never let bookkeeping failure abort the pass; worst case the order is retried next cycle.
            _log.LogWarning(ex,
                "Fulfillment reconciler could not record attempt marker for order {OrderId}",
                candidate.OrderId);
        }
    }
}
