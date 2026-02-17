namespace Circles.Market.Fulfillment.Core;

public interface IFulfillmentRunStore
{
    Task<(bool acquired, string? status)> TryBeginAsync(long chainId, string seller, string paymentReference, string orderId, CancellationToken ct);
    Task MarkOkAsync(long chainId, string seller, string paymentReference, CancellationToken ct);
    Task MarkErrorAsync(long chainId, string seller, string paymentReference, string error, CancellationToken ct);
    Task<string?> GetStatusAsync(long chainId, string seller, string paymentReference, CancellationToken ct);
}
