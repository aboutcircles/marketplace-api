namespace Circles.Market.Adapters.Unlock;

public enum UnlockFulfillmentStatus
{
    Ok,
    NotApplicable,
    Depleted,
    Error,
    Ambiguous,
    InProgress,
    AlreadyProcessed
}

public sealed class UnlockMintRecord
{
    public long ChainId { get; init; }
    public string SellerAddress { get; init; } = string.Empty;
    public string PaymentReference { get; init; } = string.Empty;
    public string OrderId { get; init; } = string.Empty;
    public string Sku { get; init; } = string.Empty;
    public string BuyerAddress { get; init; } = string.Empty;
    public string LockAddress { get; init; } = string.Empty;
    public string? TransactionHash { get; init; }
    public string? KeyId { get; init; }
    public long? ExpirationUnix { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? Warning { get; init; }
    public string? Error { get; init; }
    public string? ResponseJson { get; init; }
}
