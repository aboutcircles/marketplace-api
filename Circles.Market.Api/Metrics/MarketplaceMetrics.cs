using Prometheus;

namespace Circles.Market.Api.Metrics;

public static class MarketplaceMetrics
{
    public static readonly Counter OrdersCreated = Prometheus.Metrics.CreateCounter(
        "marketplace_orders_created_total",
        "Total number of orders checked out");

    public static readonly Counter OrdersFinalized = Prometheus.Metrics.CreateCounter(
        "marketplace_orders_finalized_total",
        "Total number of orders fully paid and confirmed");

    public static readonly Counter PaymentAmountCrc = Prometheus.Metrics.CreateCounter(
        "marketplace_payment_amount_crc_total",
        "Total CRC spent through marketplace orders");

    public static readonly Counter PaymentsIneligibleToken = Prometheus.Metrics.CreateCounter(
        "marketplace_payments_ineligible_token_total",
        "Total payment transfers recorded but not credited because the received token is not trusted by the gateway");

    public static readonly Counter PaymentsUndeterminedToken = Prometheus.Metrics.CreateCounter(
        "marketplace_payments_undetermined_token_total",
        "Payment transfers whose token-trust eligibility could not yet be determined (recorded, not credited, retried next tick)");

    public static readonly Counter GatewayTrustFetchFailures = Prometheus.Metrics.CreateCounter(
        "marketplace_gateway_trust_fetch_failures_total",
        "Failures fetching a gateway's on-chain trust list; affected payments stay undetermined until it recovers");

    public static readonly Counter PaymentsReconciled = Prometheus.Metrics.CreateCounter(
        "marketplace_payments_reconciled_total",
        "Orders re-driven to settlement by the reconciliation pass (were unpaid despite sufficient eligible payment); a sustained nonzero rate signals an upstream observe-time matching gap");

    public static readonly Counter Fulfillment = Prometheus.Metrics.CreateCounter(
        "marketplace_fulfillment_total",
        "Fulfillment outcomes on the normal (poller/hook-driven) path, labelled by outcome (succeeded|failed).",
        new CounterConfiguration { LabelNames = new[] { "outcome" } });

    public static readonly Gauge FulfillmentStranded = Prometheus.Metrics.CreateGauge(
        "marketplace_fulfillment_stranded",
        "Orders that are paid (finalized/confirmed) past the reconcile grace window but have no successful fulfillment outbox row and are still under the reconcile attempt cap. A sustained nonzero value means orders are stranded after payment — the customer paid but nothing was fulfilled. Alert on this.");

    public static readonly Counter FulfillmentReconciled = Prometheus.Metrics.CreateCounter(
        "marketplace_fulfillment_reconciled_total",
        "Stranded orders successfully re-driven to fulfillment by the fulfillment reconciler (a fulfillment outbox row appeared after re-drive). A nonzero rate means the normal fulfillment path missed orders the reconciler recovered — investigate the upstream miss.");

    public static readonly Counter FulfillmentReconcileFailed = Prometheus.Metrics.CreateCounter(
        "marketplace_fulfillment_reconcile_failed_total",
        "Reconciler re-drive attempts that did not produce a fulfillment (order still stranded; an attempt marker is recorded and the order is retried until the per-order attempt cap). Sustained increases indicate a stuck order the reconciler cannot self-heal — needs manual attention.");

    public static readonly Counter SchemaCollationMigrationFailures = Prometheus.Metrics.CreateCounter(
        "marketplace_schema_collation_migration_failures_total",
        "Best-effort COLLATE \"C\" identifier-column migration failed on startup (non-fatal). A nonzero value means identifier columns remain on a locale collation and are still exposed to glibc collation drift — most importantly a duplicate-key error here indicates live duplicate rows under a unique index. Alert on this; investigate for duplicates rather than ignore.",
        new CounterConfiguration { LabelNames = new[] { "store" } });

    public static readonly Histogram OrderValueCrc = Prometheus.Metrics.CreateHistogram(
        "marketplace_order_value_crc",
        "Distribution of order values in CRC",
        new HistogramConfiguration
        {
            Buckets = Histogram.ExponentialBuckets(1, 2, 15)
        });
}
