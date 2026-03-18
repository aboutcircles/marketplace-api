using Circles.Market.Api.Cart;
using Circles.Market.Api;

namespace Circles.Market.Api.Payments;

public sealed class OrderPaymentFlow : IOrderPaymentFlow
{
    private readonly IPaymentStore _payments;
    private readonly IOrderStore _orders;
    private readonly IOrderLifecycleHooks _hooks;
    private readonly IOrderProcessingTraceSink _trace;
    private readonly ILogger<OrderPaymentFlow> _log;

    public OrderPaymentFlow(
        IPaymentStore payments,
        IOrderStore orders,
        IOrderLifecycleHooks hooks,
        IOrderProcessingTraceSink trace,
        ILogger<OrderPaymentFlow> log)
    {
        _payments = payments ?? throw new ArgumentNullException(nameof(payments));
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
        _trace = trace ?? throw new ArgumentNullException(nameof(trace));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public Task HandleObservedTransferAsync(PaymentTransferRecord transfer, CancellationToken ct = default)
    {
        if (transfer is null)
        {
            throw new ArgumentNullException(nameof(transfer));
        }

        _payments.UpsertObservedTransfer(transfer);

        _trace.Emit(new OrderProcessingTraceEvent(
            OccurredAt: DateTimeOffset.UtcNow,
            Stage: "payment_observed",
            Status: "info",
            OrderId: null,
            PaymentReference: transfer.PaymentReference,
            ChainId: transfer.ChainId,
            SellerAddress: null,
            BuyerAddress: null,
            ReasonCode: null,
            Message: "Payment transfer observed",
            Details: null));

        var aggregated = _payments.UpsertAndGetPayment(transfer.ChainId, transfer.PaymentReference);
        if (aggregated is null) return Task.CompletedTask;

        bool hasPaymentReference = !string.IsNullOrWhiteSpace(aggregated.PaymentReference);
        if (!hasPaymentReference)
        {
            _trace.Emit(new OrderProcessingTraceEvent(
                OccurredAt: DateTimeOffset.UtcNow,
                Stage: "payment_aggregated",
                Status: "warn",
                OrderId: null,
                PaymentReference: null,
                ChainId: aggregated.ChainId,
                SellerAddress: null,
                BuyerAddress: null,
                ReasonCode: "missing_payment_reference",
                Message: "Aggregated payment had no payment reference",
                Details: null));
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

            _trace.Emit(new OrderProcessingTraceEvent(
                OccurredAt: DateTimeOffset.UtcNow,
                Stage: "order_marked_paid",
                Status: "info",
                OrderId: null,
                PaymentReference: aggregated.PaymentReference,
                ChainId: aggregated.ChainId,
                SellerAddress: null,
                BuyerAddress: null,
                ReasonCode: null,
                Message: "Order status changed to payment processing",
                Details: null));

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
                    _trace.Emit(new OrderProcessingTraceEvent(
                        OccurredAt: DateTimeOffset.UtcNow,
                        Stage: "hooks_paid_failed",
                        Status: "error",
                        OrderId: null,
                        PaymentReference: aggregated.PaymentReference,
                        ChainId: aggregated.ChainId,
                        SellerAddress: null,
                        BuyerAddress: null,
                        ReasonCode: "hook_exception",
                        Message: ex.Message,
                        Details: null));
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
            _trace.Emit(new OrderProcessingTraceEvent(
                OccurredAt: DateTimeOffset.UtcNow,
                Stage: "payment_confirm_ignored",
                Status: "info",
                OrderId: null,
                PaymentReference: paymentReference,
                ChainId: chainId,
                SellerAddress: null,
                BuyerAddress: null,
                ReasonCode: "already_confirmed_or_missing",
                Message: "Payment confirmation produced no state change",
                Details: null));
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
            _trace.Emit(new OrderProcessingTraceEvent(
                OccurredAt: DateTimeOffset.UtcNow,
                Stage: "order_marked_confirmed",
                Status: "info",
                OrderId: null,
                PaymentReference: record.PaymentReference,
                ChainId: chainId,
                SellerAddress: null,
                BuyerAddress: null,
                ReasonCode: null,
                Message: "Order marked confirmed",
                Details: null));
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
                    _trace.Emit(new OrderProcessingTraceEvent(
                        OccurredAt: DateTimeOffset.UtcNow,
                        Stage: "hooks_confirmed_failed",
                        Status: "error",
                        OrderId: null,
                        PaymentReference: record.PaymentReference,
                        ChainId: chainId,
                        SellerAddress: null,
                        BuyerAddress: null,
                        ReasonCode: "hook_exception",
                        Message: ex.Message,
                        Details: null));
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
            _trace.Emit(new OrderProcessingTraceEvent(
                OccurredAt: DateTimeOffset.UtcNow,
                Stage: "payment_finalize_ignored",
                Status: "info",
                OrderId: null,
                PaymentReference: paymentReference,
                ChainId: chainId,
                SellerAddress: null,
                BuyerAddress: null,
                ReasonCode: "already_finalized_or_missing",
                Message: "Payment finalization produced no state change",
                Details: null));
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
            _trace.Emit(new OrderProcessingTraceEvent(
                OccurredAt: DateTimeOffset.UtcNow,
                Stage: "order_marked_finalized",
                Status: "info",
                OrderId: null,
                PaymentReference: record.PaymentReference,
                ChainId: chainId,
                SellerAddress: null,
                BuyerAddress: null,
                ReasonCode: null,
                Message: "Order marked finalized",
                Details: null));

            Metrics.MarketplaceMetrics.OrdersFinalized.Inc();
            foreach (var (orderId, _, _) in _orders.GetByPaymentReference(record!.PaymentReference))
            {
                var snapshot = _orders.Get(orderId);
                if (snapshot?.TotalPaymentDue?.Price is > 0)
                {
                    Metrics.MarketplaceMetrics.PaymentAmountCrc.Inc((double)snapshot.TotalPaymentDue.Price.Value);
                }
            }

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
                    _trace.Emit(new OrderProcessingTraceEvent(
                        OccurredAt: DateTimeOffset.UtcNow,
                        Stage: "hooks_finalized_failed",
                        Status: "error",
                        OrderId: null,
                        PaymentReference: record.PaymentReference,
                        ChainId: chainId,
                        SellerAddress: null,
                        BuyerAddress: null,
                        ReasonCode: "hook_exception",
                        Message: ex.Message,
                        Details: null));
                }
            }), ct);
        }

        return Task.CompletedTask;
    }
}
