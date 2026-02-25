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

    public static readonly Histogram OrderValueCrc = Prometheus.Metrics.CreateHistogram(
        "marketplace_order_value_crc",
        "Distribution of order values in CRC",
        new HistogramConfiguration
        {
            Buckets = Histogram.ExponentialBuckets(1, 2, 15)
        });
}
