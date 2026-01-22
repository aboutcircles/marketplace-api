namespace Circles.Market.Api.Payments;

public interface IOrderPaymentFlow
{
    /// <summary>
    /// Called whenever a payment log is observed on-chain.
    /// Responsible for persisting the payment and attempting to mark
    /// the corresponding order as paid.
    /// </summary>
    Task HandleObservedTransferAsync(PaymentTransferRecord transfer, CancellationToken ct = default);

    /// <summary>
    /// Optional intermediate stage: called when a payment is considered
    /// confirmed but not yet finalized (e.g. after N confirmations).
    /// Not wired up yet, but gives you a clean hook if you want a separate
    /// "confirmed" concept.
    /// </summary>
    Task HandleConfirmedAsync(
        long chainId,
        string paymentReference,
        long blockNumber,
        DateTimeOffset confirmedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Called when a payment is considered finalized (enough confirmations).
    /// Responsible for marking both the payment and the order as finalized.
    /// </summary>
    Task HandleFinalizationAsync(
        long chainId,
        string paymentReference,
        DateTimeOffset finalizedAt,
        CancellationToken ct = default);
}
