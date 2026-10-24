namespace Circles.Market.Adapters.Odoo.Conversion;

/// <summary>
/// Represents a fully resolved dCRC→EUR conversion snapshot.
/// Uses the Circles metrics-api pricing endpoint which accounts for demurrage.
/// </summary>
public sealed class ConversionSnapshot
{
    public string OrderId { get; set; } = string.Empty;
    public string PaymentReference { get; set; } = string.Empty;
    public string? PaymentTimestamp { get; set; }

    /// <summary>CRC amount in wei (raw).</summary>
    public string? AmountWei { get; set; }

    /// <summary>CRC amount as decimal (wei / 1e18).</summary>
    public decimal? AmountCrc { get; set; }

    /// <summary>sCRC/WXDAI price from Balancer (1 sCRC = N xDAI).</summary>
    public decimal? ScrToXdaiRate { get; set; }

    /// <summary>Demurrage conversion factor (1 sCRC = N dCRC).</summary>
    public decimal? ConversionFactor { get; set; }

    /// <summary>dCRC/xDAI rate (= sCRC/xDAI ÷ conv_factor). 1 dCRC = N xDAI.</summary>
    public decimal? DcrcToXdaiRate { get; set; }

    /// <summary>xDAI/EUR rate.</summary>
    public decimal? XdaiToEurRate { get; set; }

    /// <summary>EUR equivalent (AmountCrc × dcrc_eur).</summary>
    public decimal? EurEquivalent { get; set; }

    /// <summary>Date used for price lookup (YYYY-MM-DD).</summary>
    public string? PriceDate { get; set; }

    /// <summary>When this conversion was generated.</summary>
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Price source (e.g. "balancer_live").</summary>
    public string? PricingSource { get; set; }
}

/// <summary>
/// Fetches date-specific dCRC pricing from the Circles metrics-api.
/// </summary>
public interface ICirclesPricingService
{
    /// <summary>
    /// Compute a full conversion snapshot from the payment metadata in a fulfilment request.
    /// Returns null if any step fails (never throws).
    /// </summary>
    Task<ConversionSnapshot?> ComputeAsync(
        string orderId,
        string paymentReference,
        string? amountWei,
        string? paymentTimestamp,
        CancellationToken ct = default);
}