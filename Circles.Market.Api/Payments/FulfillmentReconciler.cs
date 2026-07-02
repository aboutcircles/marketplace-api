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
/// Supplies the set of orders that are paid/finalized but have no successful fulfillment.
/// Abstracted so the reconciler's orchestration is unit-testable without a database (the
/// concrete SQL implementation follows the same "not unit-tested" convention as the poller SQL).
/// </summary>
public interface IStrandedFulfillmentSource
{
    Task<IReadOnlyList<StrandedOrder>> GetStrandedAsync(
        int graceMinutes,
        int maxAttempts,
        int maxAgeDays,
        int limit,
        CancellationToken ct);
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

    public async Task<IReadOnlyList<StrandedOrder>> GetStrandedAsync(
        int graceMinutes,
        int maxAttempts,
        int maxAgeDays,
        int limit,
        CancellationToken ct)
    {
        var result = new List<StrandedOrder>();
        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT o.order_id, o.payment_reference
FROM orders o
WHERE o.payment_reference IS NOT NULL
  AND COALESCE(o.finalized_at, o.confirmed_at) IS NOT NULL
  AND COALESCE(o.finalized_at, o.confirmed_at) < now() - make_interval(mins => @grace)
  AND COALESCE(o.finalized_at, o.confirmed_at) > now() - make_interval(days => @maxAge)
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
        cmd.Parameters.AddWithValue("@grace", graceMinutes);
        cmd.Parameters.AddWithValue("@maxAttempts", maxAttempts);
        cmd.Parameters.AddWithValue("@maxAge", maxAgeDays);
        cmd.Parameters.AddWithValue("@limit", limit);

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
    }

    private static int ReadPositiveInt(string envName, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(envName);
        return int.TryParse(raw, out var v) && v > 0 ? v : fallback;
    }

    public async Task<FulfillmentReconcileResult> ReconcileOnceAsync(CancellationToken ct)
    {
        var candidates = await _source.GetStrandedAsync(_graceMinutes, _maxAttempts, _maxAgeDays, _limit, ct);

        // Dedupe by payment reference: the hooks fan out to every order sharing the reference, so
        // firing once per reference is sufficient (and avoids double-firing a shared reference).
        var byReference = new Dictionary<string, StrandedOrder>(StringComparer.Ordinal);
        foreach (var c in candidates)
        {
            byReference.TryAdd(c.PaymentReference, c);
        }

        MarketplaceMetrics.FulfillmentStranded.Set(byReference.Count);

        if (byReference.Count == 0)
        {
            return new FulfillmentReconcileResult(0, 0, 0, _enabled);
        }

        if (!_enabled)
        {
            // Shadow mode: surface what WOULD be re-driven so it can be vetted before enabling.
            _log.LogWarning(
                "Fulfillment reconciler (SHADOW, disabled): {Count} stranded order(s) detected but NOT re-driven. " +
                "Set FULFILLMENT_RECONCILE_ENABLED=true after verifying the candidates. Sample: {Sample}",
                byReference.Count,
                string.Join(", ", byReference.Values.Take(10).Select(o => o.OrderId)));
            return new FulfillmentReconcileResult(byReference.Count, 0, 0, false);
        }

        _log.LogWarning(
            "Fulfillment reconciler (ACTIVE): re-driving {Count} stranded order(s)",
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
