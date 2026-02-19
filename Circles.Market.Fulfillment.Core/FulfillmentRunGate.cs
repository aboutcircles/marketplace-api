namespace Circles.Market.Fulfillment.Core;

public enum FulfillmentRunGateState
{
    Acquired,
    AlreadyProcessed,
    InProgress,
    Unavailable
}

public sealed record FulfillmentRunGateResult(
    FulfillmentRunGateState State,
    string? ExistingStatus);

public static class FulfillmentRunGate
{
    public static async Task<FulfillmentRunGateResult> TryAcquireAsync(
        IFulfillmentRunStore store,
        long chainId,
        string seller,
        string paymentReference,
        string orderId,
        CancellationToken ct)
    {
        var (acquired, existingStatus) = await store.TryBeginAsync(chainId, seller, paymentReference, orderId, ct);
        if (acquired)
        {
            return new FulfillmentRunGateResult(FulfillmentRunGateState.Acquired, existingStatus);
        }

        if (string.Equals(existingStatus, "ok", StringComparison.OrdinalIgnoreCase))
        {
            return new FulfillmentRunGateResult(FulfillmentRunGateState.AlreadyProcessed, existingStatus);
        }

        if (string.Equals(existingStatus, "started", StringComparison.OrdinalIgnoreCase))
        {
            return new FulfillmentRunGateResult(FulfillmentRunGateState.InProgress, existingStatus);
        }

        return new FulfillmentRunGateResult(FulfillmentRunGateState.Unavailable, existingStatus);
    }
}
