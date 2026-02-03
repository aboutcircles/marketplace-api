using Circles.Market.Api.Cart;
using Circles.Market.Api.Fulfillment;
using Circles.Market.Api.Routing;

namespace Circles.Market.Api.Payments;

public sealed class SseOrderLifecycleHooks : IOrderLifecycleHooks
{
    private readonly IOrderStatusEventBus _bus;
    private readonly IOrderStore _orders;
    private readonly IOrderFulfillmentClient _fulfillment;
    private readonly IMarketRouteStore _routes;
    private readonly ILogger<SseOrderLifecycleHooks> _log;

    public SseOrderLifecycleHooks(
        IOrderStatusEventBus bus,
        IOrderStore orders,
        IOrderFulfillmentClient fulfillment,
        IMarketRouteStore routes,
        ILogger<SseOrderLifecycleHooks> log)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _fulfillment = fulfillment ?? throw new ArgumentNullException(nameof(fulfillment));
        _routes = routes ?? throw new ArgumentNullException(nameof(routes));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public Task OnPaidAsync(string paymentReference, long chainId, string txHash, int logIndex, DateTimeOffset paidAt, CancellationToken ct = default)
        => Task.CompletedTask;

    public async Task OnConfirmedAsync(string paymentReference, DateTimeOffset confirmedAt, CancellationToken ct = default)
    {
        await RunFulfillmentAsync(paymentReference, trigger: "confirmed", confirmedAt, ct);
    }

    public async Task OnFinalizedAsync(string paymentReference, DateTimeOffset finalizedAt, CancellationToken ct = default)
    {
        await RunFulfillmentAsync(paymentReference, trigger: "finalized", finalizedAt, ct);
    }

    public async Task OnStatusChangedAsync(string orderKey, string? oldStatus, string newStatus, DateTimeOffset changedAt, CancellationToken ct = default)
    {
        try
        {
            foreach (var meta in _orders.GetByPaymentReference(orderKey))
            {
                if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();

                // Publish buyer-scoped event once
                var buyerEvt = new OrderStatusEvent(
                    OrderId: meta.OrderId,
                    PaymentReference: orderKey,
                    OldStatus: oldStatus,
                    NewStatus: newStatus,
                    ChangedAt: changedAt,
                    BuyerAddress: meta.BuyerAddress,
                    BuyerChainId: meta.BuyerChainId,
                    SellerAddress: null,
                    SellerChainId: null);
                await _bus.PublishAsync(buyerEvt, ct);

                // Also publish seller-scoped events for each distinct seller from the order snapshot
                var snapshot = _orders.Get(meta.OrderId);
                if (snapshot is null) continue;

                var seen = new HashSet<(string addr, long chain)>();
                foreach (var offer in snapshot.AcceptedOffer)
                {
                    var id = offer.Seller?.Id;
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    // Expect format eip155:{chain}:{address}
                    var parts = id.Split(':');
                    if (parts.Length == 3 && string.Equals(parts[0], "eip155", StringComparison.OrdinalIgnoreCase)
                        && long.TryParse(parts[1], out var sChain))
                    {
                        string sAddr = parts[2];
                        if (string.IsNullOrWhiteSpace(sAddr)) continue;
                        var key = (addr: sAddr.ToLowerInvariant(), chain: sChain);
                        if (seen.Add(key))
                        {
                            var sellerEvt = new OrderStatusEvent(
                                OrderId: meta.OrderId,
                                PaymentReference: orderKey,
                                OldStatus: oldStatus,
                                NewStatus: newStatus,
                                ChangedAt: changedAt,
                                BuyerAddress: meta.BuyerAddress,
                                BuyerChainId: meta.BuyerChainId,
                                SellerAddress: key.addr,
                                SellerChainId: key.chain);
                            await _bus.PublishAsync(sellerEvt, ct);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to publish order status change for ref={OrderKey}", orderKey);
        }
    }

    private async Task RunFulfillmentAsync(string paymentReference, string trigger, DateTimeOffset at, CancellationToken ct)
    {
        try
        {
            foreach (var meta in _orders.GetByPaymentReference(paymentReference))
            {
                if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();
                var snapshot = _orders.Get(meta.OrderId);
                if (snapshot is null) continue;
                string orderId = snapshot.OrderNumber;
                string payRef = snapshot.PaymentReference ?? paymentReference;

                int offers = snapshot.AcceptedOffer?.Count ?? 0;
                int items = snapshot.OrderedItem?.Count ?? 0;
                int n = Math.Min(offers, items);

                for (int i = 0; i < n; i++)
                {
                    var offer = snapshot.AcceptedOffer![i];
                    var item = snapshot.OrderedItem![i];

                    string? sellerId = offer.Seller?.Id;
                    if (!TryParseEip155SellerId(sellerId, out long sellerChain, out string sellerAddr))
                    {
                        continue;
                    }

                    string? sku = item.OrderedItem?.Sku;
                    if (string.IsNullOrWhiteSpace(sku))
                    {
                        continue;
                    }

                    string skuNorm = sku.Trim().ToLowerInvariant();

                    var endpoint = await _routes.TryResolveUpstreamAsync(
                        sellerChain,
                        sellerAddr,
                        skuNorm,
                        MarketServiceKind.Fulfillment,
                        ct);

                    if (string.IsNullOrWhiteSpace(endpoint))
                    {
                        _log.LogWarning("Fulfillment skipped: no configured endpoint for chain={Chain} seller={Seller} sku={Sku}",
                            sellerChain, sellerAddr, skuNorm);
                        continue;
                    }

                    string effectiveTrigger = string.IsNullOrWhiteSpace(offer.CirclesFulfillmentTrigger)
                        ? "finalized"
                        : offer.CirclesFulfillmentTrigger.Trim().ToLowerInvariant();

                    if (!string.Equals(effectiveTrigger, trigger, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        var payload = await _fulfillment.FulfillAsync(endpoint!, orderId, payRef, snapshot, trigger, ct);
                        _orders.AddOutboxItem(orderId, "fulfillment", payload);
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Fulfillment failed for order {OrderId} endpoint={Endpoint}", orderId, endpoint);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "RunFulfillmentAsync failed for ref={Ref} trigger={Trigger}", paymentReference, trigger);
        }
    }

    private static bool TryParseEip155SellerId(string? id, out long chainId, out string addr)
    {
        chainId = 0;
        addr = string.Empty;

        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var parts = id.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool partsOk = parts.Length == 3;
        if (!partsOk)
        {
            return false;
        }

        bool prefixOk = string.Equals(parts[0], "eip155", StringComparison.OrdinalIgnoreCase);
        bool chainOk = long.TryParse(parts[1], out var parsedChain);
        if (!prefixOk || !chainOk)
        {
            return false;
        }

        string parsedAddr = parts[2].Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(parsedAddr))
        {
            return false;
        }

        chainId = parsedChain;
        addr = parsedAddr;
        return true;
    }
}
