using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Circles.Market.Api.Cart;

public interface IOrderAccessService
{
    Task<OrderSnapshot?> GetOrderForBuyerAsync(
        string orderId,
        string buyerAddress,
        long chainId,
        CancellationToken ct = default);

    Task<IReadOnlyList<OrderSnapshot>> GetOrdersForBuyerAsync(
        string buyerAddress,
        long chainId,
        int page,
        int pageSize,
        CancellationToken ct = default);

    // NEW: buyer-scoped status history retrieval
    Task<OrderStatusHistoryDto?> GetOrderStatusHistoryForBuyerAsync(
        string orderId,
        string buyerAddress,
        long chainId,
        CancellationToken ct = default);

    // SELLER-SAFE READS (return SellerOrderDto only)
    Task<IReadOnlyList<Circles.Market.Api.Cart.SellerVisibility.SellerOrderDto>> GetOrdersForSellerAsync(
        string sellerAddress,
        long chainId,
        int page,
        int pageSize,
        CancellationToken ct = default);

    Task<Circles.Market.Api.Cart.SellerVisibility.SellerOrderDto?> GetOrderForSellerAsync(
        string orderId,
        string sellerAddress,
        long chainId,
        CancellationToken ct = default);
}

// DTOs for status history endpoint
public sealed class OrderStatusHistoryDto
{
    public string OrderId { get; set; } = string.Empty;
    public List<OrderStatusEventDto> Events { get; set; } = new();
}

public sealed class OrderStatusEventDto
{
    public string? OldStatus { get; set; }
    public string NewStatus { get; set; } = string.Empty;
    public DateTimeOffset ChangedAt { get; set; }
}

public sealed class OrderAccessService : IOrderAccessService
{
    private readonly IOrderStore _orders;
    private readonly ILogger<OrderAccessService> _log;

    public OrderAccessService(IOrderStore orders, ILogger<OrderAccessService> log)
    {
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public Task<OrderSnapshot?> GetOrderForBuyerAsync(
        string orderId,
        string buyerAddress,
        long chainId,
        CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(orderId)) return Task.FromResult<OrderSnapshot?>(null);
        if (string.IsNullOrWhiteSpace(buyerAddress)) return Task.FromResult<OrderSnapshot?>(null);

        var owner = _orders.GetOwnerByOrderId(orderId);
        if (!owner.HasValue) return Task.FromResult<OrderSnapshot?>(null);

        string? dbBuyerAddr = owner.Value.BuyerAddress;
        long? dbBuyerChain = owner.Value.BuyerChainId;
        bool buyerMatches =
            !string.IsNullOrWhiteSpace(dbBuyerAddr) &&
            dbBuyerChain.HasValue &&
            string.Equals(dbBuyerAddr, buyerAddress, StringComparison.OrdinalIgnoreCase) &&
            dbBuyerChain.Value == chainId;

        if (!buyerMatches) return Task.FromResult<OrderSnapshot?>(null);

        var snapshot = _orders.Get(orderId);
        return Task.FromResult(snapshot);
    }

    public Task<IReadOnlyList<OrderSnapshot>> GetOrdersForBuyerAsync(
        string buyerAddress,
        long chainId,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(buyerAddress))
            return Task.FromResult<IReadOnlyList<OrderSnapshot>>(Array.Empty<OrderSnapshot>());

        int pageNumber = page < 1 ? 1 : page;
        int size = Math.Clamp(pageSize, MarketConstants.Defaults.PageSizeMin, MarketConstants.Defaults.PageSizeMax);
        var items = _orders.GetByBuyer(buyerAddress, chainId, pageNumber, size).ToList();
        IReadOnlyList<OrderSnapshot> result = items;
        return Task.FromResult(result);
    }

    public Task<OrderStatusHistoryDto?> GetOrderStatusHistoryForBuyerAsync(
        string orderId,
        string buyerAddress,
        long chainId,
        CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(orderId) || string.IsNullOrWhiteSpace(buyerAddress))
        {
            return Task.FromResult<OrderStatusHistoryDto?>(null);
        }

        var owner = _orders.GetOwnerByOrderId(orderId);
        if (!owner.HasValue)
        {
            return Task.FromResult<OrderStatusHistoryDto?>(null);
        }

        string? dbBuyer = owner.Value.BuyerAddress;
        long? dbChain = owner.Value.BuyerChainId;

        bool matches =
            !string.IsNullOrWhiteSpace(dbBuyer) &&
            dbChain.HasValue &&
            string.Equals(dbBuyer, buyerAddress, StringComparison.OrdinalIgnoreCase) &&
            dbChain.Value == chainId;

        if (!matches)
        {
            return Task.FromResult<OrderStatusHistoryDto?>(null);
        }

        var entries = _orders.GetStatusHistory(orderId).ToList();

        var dto = new OrderStatusHistoryDto
        {
            OrderId = orderId,
            Events = entries
                .Select(e => new OrderStatusEventDto
                {
                    OldStatus = e.OldStatus,
                    NewStatus = e.NewStatus,
                    ChangedAt = e.ChangedAt
                })
                .ToList()
        };

        return Task.FromResult<OrderStatusHistoryDto?>(dto);
    }

    // SELLER-SAFE READS
    public Task<IReadOnlyList<Circles.Market.Api.Cart.SellerVisibility.SellerOrderDto>> GetOrdersForSellerAsync(
        string sellerAddress,
        long chainId,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(sellerAddress))
            return Task.FromResult<IReadOnlyList<Circles.Market.Api.Cart.SellerVisibility.SellerOrderDto>>(Array.Empty<Circles.Market.Api.Cart.SellerVisibility.SellerOrderDto>());

        int pageNumber = page < 1 ? 1 : page;
        int size = Math.Clamp(pageSize, MarketConstants.Defaults.PageSizeMin, MarketConstants.Defaults.PageSizeMax);

        var ids = _orders.GetOrderIdsBySeller(sellerAddress, chainId, pageNumber, size).ToList();
        var result = new List<Circles.Market.Api.Cart.SellerVisibility.SellerOrderDto>(ids.Count);
        foreach (var id in ids)
        {
            var snapshot = _orders.GetInternal(id, includeOutbox: false);
            if (snapshot is null) continue;
            var indices = _orders.GetOrderLineIndicesForSeller(id, sellerAddress, chainId);
            if (indices is null || indices.Count == 0) continue;
            var dto = Circles.Market.Api.Cart.SellerVisibility.SellerOrderViewBuilder.Build(snapshot, indices);
            result.Add(dto);
        }

        return Task.FromResult<IReadOnlyList<Circles.Market.Api.Cart.SellerVisibility.SellerOrderDto>>(result);
    }

    public Task<Circles.Market.Api.Cart.SellerVisibility.SellerOrderDto?> GetOrderForSellerAsync(
        string orderId,
        string sellerAddress,
        long chainId,
        CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(orderId) || string.IsNullOrWhiteSpace(sellerAddress))
            return Task.FromResult<Circles.Market.Api.Cart.SellerVisibility.SellerOrderDto?>(null);

        if (!_orders.OrderContainsSeller(orderId, sellerAddress, chainId))
            return Task.FromResult<Circles.Market.Api.Cart.SellerVisibility.SellerOrderDto?>(null);

        var snapshot = _orders.GetInternal(orderId, includeOutbox: false);
        if (snapshot is null)
            return Task.FromResult<Circles.Market.Api.Cart.SellerVisibility.SellerOrderDto?>(null);

        var indices = _orders.GetOrderLineIndicesForSeller(orderId, sellerAddress, chainId);
        if (indices.Count == 0)
            return Task.FromResult<Circles.Market.Api.Cart.SellerVisibility.SellerOrderDto?>(null);

        var dto = Circles.Market.Api.Cart.SellerVisibility.SellerOrderViewBuilder.Build(snapshot, indices);
        return Task.FromResult<Circles.Market.Api.Cart.SellerVisibility.SellerOrderDto?>(dto);
    }
}
