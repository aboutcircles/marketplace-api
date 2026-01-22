namespace Circles.Market.Api.Payments;

public sealed record OrderStatusEvent(
    string OrderId,
    string? PaymentReference,
    string? OldStatus,
    string NewStatus,
    DateTimeOffset ChangedAt,
    string? BuyerAddress,
    long? BuyerChainId,
    string? SellerAddress,
    long? SellerChainId
);
