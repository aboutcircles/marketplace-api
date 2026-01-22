namespace Circles.Market.Api.Payments;

/// <summary>
/// Lifecycle hooks invoked after successful order state transitions.
/// Implementations should be idempotent. Exceptions must not bubble to callers.
/// </summary>
public interface IOrderLifecycleHooks
{
    /// <summary>
    /// Invoked after an order is marked paid/processing.
    /// </summary>
    Task OnPaidAsync(
        string paymentReference,
        long chainId,
        string txHash,
        int logIndex,
        DateTimeOffset paidAt,
        CancellationToken ct = default);

    /// <summary>
    /// Invoked after an order is marked confirmed (optional intermediate stage).
    /// </summary>
    Task OnConfirmedAsync(
        string paymentReference,
        DateTimeOffset confirmedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Invoked after an order is marked finalized/payment complete.
    /// </summary>
    Task OnFinalizedAsync(
        string paymentReference,
        DateTimeOffset finalizedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Generic status change notification. Use for analytics/monitoring.
    /// </summary>
    Task OnStatusChangedAsync(
        string orderKey,
        string? oldStatus,
        string newStatus,
        DateTimeOffset changedAt,
        CancellationToken ct = default);
}

/// <summary>
/// Default no-op hooks so the app can run without custom behavior.
/// </summary>
public sealed class NoopOrderLifecycleHooks : IOrderLifecycleHooks
{
    public Task OnPaidAsync(string paymentReference, long chainId, string txHash, int logIndex, DateTimeOffset paidAt, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task OnConfirmedAsync(string paymentReference, DateTimeOffset confirmedAt, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task OnFinalizedAsync(string paymentReference, DateTimeOffset finalizedAt, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task OnStatusChangedAsync(string orderKey, string? oldStatus, string newStatus, DateTimeOffset changedAt, CancellationToken ct = default)
        => Task.CompletedTask;
}
