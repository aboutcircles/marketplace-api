using Circles.Market.Api.Cart;
using Circles.Market.Api.Fulfillment;
using Circles.Market.Api.Payments;
using Circles.Market.Api.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Circles.Market.Tests;

[TestFixture]
public class SseOrderLifecycleHooksTests
{
    private Mock<IOrderStatusEventBus> _busMock;
    private Mock<IOrderStore> _ordersMock;
    private Mock<IOrderFulfillmentClient> _fulfillmentMock;
    private Mock<IMarketRouteStore> _routesMock;
    private SseOrderLifecycleHooks _sut;

    [SetUp]
    public void SetUp()
    {
        _busMock = new Mock<IOrderStatusEventBus>();
        _ordersMock = new Mock<IOrderStore>();
        _fulfillmentMock = new Mock<IOrderFulfillmentClient>();
        _routesMock = new Mock<IMarketRouteStore>();
        _sut = new SseOrderLifecycleHooks(
            _busMock.Object,
            _ordersMock.Object,
            _fulfillmentMock.Object,
            _routesMock.Object,
            NullLogger<SseOrderLifecycleHooks>.Instance);
    }

    [Test]
    public async Task RunFulfillmentAsync_NullTrigger_OnlyRunsOnFinalized()
    {
        // Arrange
        var payRef = "pay_123";
        var orderId = "ord_123";
        var sellerAddr = "0xseller" + new string('1', 40);
        var sku = "test-sku";

        var snapshot = new OrderSnapshot
        {
            OrderNumber = orderId,
            PaymentReference = payRef,
            AcceptedOffer = new List<OfferSnapshot>
            {
                new()
                {
                    Seller = new SchemaOrgOrgId { Type = "Organization", Id = $"eip155:100:{sellerAddr}" },
                    CirclesFulfillmentEndpoint = null, // No longer used
                    CirclesFulfillmentTrigger = null // Should default to finalized
                }
            },
            OrderedItem = new List<OrderItemLine>
            {
                new() { Type = "OrderItem", OrderQuantity = 1, OrderedItem = new OrderedItemRef { Type = "Product", Sku = sku } }
            }
        };

        _ordersMock.Setup(x => x.GetByPaymentReference(payRef))
            .Returns(new[] { (orderId, (string?)"0xbuyer", (long?)100) });
        _ordersMock.Setup(x => x.Get(orderId)).Returns(snapshot);

        // Route store returns fulfillment endpoint from DB
        var sellerAddrLower = sellerAddr.ToLowerInvariant();
        var skuLower = sku.Trim().ToLowerInvariant();
        _routesMock.Setup(x => x.TryResolveUpstreamAsync(
                100,
                sellerAddrLower,
                skuLower,
                MarketServiceKind.Fulfillment,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://example.com/fulfill");

        // Act
        await _sut.OnConfirmedAsync(payRef, DateTimeOffset.Now);

        // Assert - should not run on confirmed (trigger is null/defaults to finalized)
        _fulfillmentMock.Verify(x => x.FulfillAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<OrderSnapshot>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Act
        await _sut.OnFinalizedAsync(payRef, DateTimeOffset.Now);

        // Assert - should run on finalized with endpoint from DB (not snapshot)
        _fulfillmentMock.Verify(x => x.FulfillAsync(
            "https://example.com/fulfill", orderId, payRef, snapshot, "finalized", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task RunFulfillmentAsync_ExplicitConfirmed_OnlyRunsOnConfirmed()
    {
        // Arrange
        var payRef = "pay_456";
        var orderId = "ord_456";
        var sellerAddr = "0xseller" + new string('2', 40);
        var sku = "test-sku-456";

        var snapshot = new OrderSnapshot
        {
            OrderNumber = orderId,
            PaymentReference = payRef,
            AcceptedOffer = new List<OfferSnapshot>
            {
                new()
                {
                    Seller = new SchemaOrgOrgId { Type = "Organization", Id = $"eip155:100:{sellerAddr}" },
                    CirclesFulfillmentEndpoint = null, // No longer used
                    CirclesFulfillmentTrigger = "confirmed"
                }
            },
            OrderedItem = new List<OrderItemLine>
            {
                new() { Type = "OrderItem", OrderQuantity = 1, OrderedItem = new OrderedItemRef { Type = "Product", Sku = sku } }
            }
        };

        _ordersMock.Setup(x => x.GetByPaymentReference(payRef))
            .Returns(new[] { (orderId, (string?)"0xbuyer", (long?)100) });
        _ordersMock.Setup(x => x.Get(orderId)).Returns(snapshot);

        // Route store returns fulfillment endpoint from DB
        var sellerAddrLower = sellerAddr.ToLowerInvariant();
        var skuLower = sku.Trim().ToLowerInvariant();
        _routesMock.Setup(x => x.TryResolveUpstreamAsync(
                100,
                sellerAddrLower,
                skuLower,
                MarketServiceKind.Fulfillment,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://example.com/fulfill");

        // Act
        await _sut.OnConfirmedAsync(payRef, DateTimeOffset.Now);

        // Assert - should run on confirmed with endpoint from DB
        _fulfillmentMock.Verify(x => x.FulfillAsync(
            "https://example.com/fulfill", orderId, payRef, snapshot, "confirmed", It.IsAny<CancellationToken>()),
            Times.Once);

        // Act
        await _sut.OnFinalizedAsync(payRef, DateTimeOffset.Now);

        // Assert - should not run on finalized (trigger is confirmed)
        _fulfillmentMock.Verify(x => x.FulfillAsync(
            "https://example.com/fulfill", orderId, payRef, snapshot, "finalized", It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
