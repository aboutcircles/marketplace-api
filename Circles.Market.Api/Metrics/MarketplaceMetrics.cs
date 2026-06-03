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

    public static readonly Histogram OrderValueCrc = Prometheus.Metrics.CreateHistogram(
        "marketplace_order_value_crc",
        "Distribution of order values in CRC",
        new HistogramConfiguration
        {
            Buckets = Histogram.ExponentialBuckets(1, 2, 15)
        });
}
