using Circles.Market.Api.Cart;
using Circles.Market.Api;

namespace Circles.Market.Api.Payments;

public sealed class OrderPaymentFlow : IOrderPaymentFlow
{
    private readonly IPaymentStore _payments;
    private readonly IOrderStore _orders;
    private readonly IOrderLifecycleHooks _hooks;
    private readonly ILogger<OrderPaymentFlow> _log;

    public OrderPaymentFlow(
        IPaymentStore payments,
        IOrderStore orders,
        IOrderLifecycleHooks hooks,
        ILogger<OrderPaymentFlow> log)
    {
        _payments = payments ?? throw new ArgumentNullException(nameof(payments));
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public Task HandleObservedTransferAsync(PaymentTransferRecord transfer, CancellationToken ct = default)
    {
        if (transfer is null)
        {
            throw new ArgumentNullException(nameof(transfer));
        }

        _payments.UpsertObservedTransfer(transfer);

        var aggregated = _payments.UpsertAndGetPayment(transfer.ChainId, transfer.PaymentReference);
        if (aggregated is null) return Task.CompletedTask;

        bool hasPaymentReference = !string.IsNullOrWhiteSpace(aggregated.PaymentReference);
        if (!hasPaymentReference)
        {
            return Task.CompletedTask;
        }

        bool changed = _orders.TryMarkPaidByReference(
            paymentReference: aggregated.PaymentReference,
            paidChainId: aggregated.ChainId,
            txHash: aggregated.FirstTxHash ?? string.Empty,
            logIndex: aggregated.FirstLogIndex ?? 0,
            gatewayAddress: aggregated.GatewayAddress,
            amountWei: aggregated.TotalAmountWei,
            paidAt: aggregated.CreatedAt);

        if (changed)
        {
            _log.LogInformation(
                "Matched payment to order: ref={Ref} tx={Tx} log={Log}",
                aggregated.PaymentReference,
                aggregated.FirstTxHash,
                aggregated.FirstLogIndex);

            // Fire hooks: Paid and StatusChanged(PaymentProcessing)
            _ = Task.Run((Func<Task>)(async () =>
            {
                try
                {
                    await _hooks.OnPaidAsync(
                        aggregated.PaymentReference!,
                        aggregated.ChainId,
                        aggregated.FirstTxHash ?? string.Empty,
                        aggregated.FirstLogIndex ?? 0,
                        aggregated.CreatedAt,
                        ct);

                    await _hooks.OnStatusChangedAsync(
                        orderKey: aggregated.PaymentReference!,
                        oldStatus: null,
                        newStatus: StatusUris.PaymentProcessing,
                        changedAt: aggregated.CreatedAt,
                        ct: ct);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "OrderLifecycleHooks Paid/StatusChanged failed for ref={Ref}", aggregated.PaymentReference);
                }
            }), ct);
        }

        return Task.CompletedTask;
    }

    public Task HandleConfirmedAsync(
        long chainId,
        string paymentReference,
        long blockNumber,
        DateTimeOffset confirmedAt,
        CancellationToken ct = default)
    {
        bool updatedPayment = _payments.MarkConfirmed(chainId, paymentReference, blockNumber, confirmedAt);

        if (!updatedPayment)
        {
            return Task.CompletedTask;
        }

        var record = _payments.GetPayment(chainId, paymentReference);
        bool hasPaymentReference = record is not null && !string.IsNullOrWhiteSpace(record.PaymentReference);
        if (!hasPaymentReference)
        {
            return Task.CompletedTask;
        }

        bool updatedOrder = _orders.TryMarkConfirmedByReference(record!.PaymentReference, confirmedAt);
        if (updatedOrder)
        {
            _log.LogInformation("Marked order confirmed: ref={Ref}", record!.PaymentReference);
            // Fire hook: Confirmed (no status change IRI today)
            _ = Task.Run(async () =>
            {
                try
                {
                    await _hooks.OnConfirmedAsync(record.PaymentReference!, confirmedAt, ct);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "OrderLifecycleHooks Confirmed failed for ref={Ref}", record.PaymentReference);
                }
            }, ct);
        }

        return Task.CompletedTask;
    }

    public Task HandleFinalizationAsync(
        long chainId,
        string paymentReference,
        DateTimeOffset finalizedAt,
        CancellationToken ct = default)
    {
        bool updatedPayment = _payments.MarkFinalized(chainId, paymentReference, finalizedAt);

        if (!updatedPayment)
        {
            return Task.CompletedTask;
        }

        var record = _payments.GetPayment(chainId, paymentReference);
        bool hasPaymentReference = record is not null && !string.IsNullOrWhiteSpace(record.PaymentReference);
        if (!hasPaymentReference)
        {
            return Task.CompletedTask;
        }

        bool updatedOrder = _orders.TryMarkFinalizedByReference(record!.PaymentReference, finalizedAt);
        if (updatedOrder)
        {
            _log.LogInformation("Marked order finalized: ref={Ref}", record!.PaymentReference);
            // Fire hooks: Finalized and StatusChanged(PaymentComplete)
            _ = Task.Run((Func<Task>)(async () =>
            {
                try
                {
                    await _hooks.OnFinalizedAsync(record.PaymentReference!, finalizedAt, ct);
                    await _hooks.OnStatusChangedAsync(
                        orderKey: record.PaymentReference!,
                        oldStatus: StatusUris.PaymentProcessing,
                        newStatus: StatusUris.PaymentComplete,
                        changedAt: finalizedAt,
                        ct: ct);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "OrderLifecycleHooks Finalized/StatusChanged failed for ref={Ref}", record.PaymentReference);
                }
            }), ct);
        }

        return Task.CompletedTask;
    }
}
