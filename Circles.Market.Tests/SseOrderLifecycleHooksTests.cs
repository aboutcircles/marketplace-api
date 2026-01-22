using Circles.Market.Api.Cart;
using Circles.Market.Api.Fulfillment;
using Circles.Market.Api.Payments;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Circles.Market.Tests;

[TestFixture]
public class SseOrderLifecycleHooksTests
{
    private Mock<IOrderStatusEventBus> _busMock;
    private Mock<IOrderStore> _ordersMock;
    private Mock<IOrderFulfillmentClient> _fulfillmentMock;
    private SseOrderLifecycleHooks _sut;

    [SetUp]
    public void SetUp()
    {
        _busMock = new Mock<IOrderStatusEventBus>();
        _ordersMock = new Mock<IOrderStore>();
        _fulfillmentMock = new Mock<IOrderFulfillmentClient>();
        _sut = new SseOrderLifecycleHooks(
            _busMock.Object,
            _ordersMock.Object,
            _fulfillmentMock.Object,
            NullLogger<SseOrderLifecycleHooks>.Instance);
    }

    [Test]
    public async Task RunFulfillmentAsync_NullTrigger_OnlyRunsOnFinalized()
    {
        // Arrange
        var payRef = "pay_123";
        var orderId = "ord_123";
        var snapshot = new OrderSnapshot
        {
            OrderNumber = orderId,
            PaymentReference = payRef,
            AcceptedOffer = new List<OfferSnapshot>
            {
                new()
                {
                    CirclesFulfillmentEndpoint = "https://example.com/fulfill",
                    CirclesFulfillmentTrigger = null // Should default to finalized
                }
            }
        };

        _ordersMock.Setup(x => x.GetByPaymentReference(payRef))
            .Returns(new[] { (orderId, (string?)"0xbuyer", (long?)100) });
        _ordersMock.Setup(x => x.Get(orderId)).Returns(snapshot);

        // Act
        await _sut.OnConfirmedAsync(payRef, DateTimeOffset.Now);

        // Assert
        _fulfillmentMock.Verify(x => x.FulfillAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<OrderSnapshot>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Act
        await _sut.OnFinalizedAsync(payRef, DateTimeOffset.Now);

        // Assert
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
        var snapshot = new OrderSnapshot
        {
            OrderNumber = orderId,
            PaymentReference = payRef,
            AcceptedOffer = new List<OfferSnapshot>
            {
                new()
                {
                    CirclesFulfillmentEndpoint = "https://example.com/fulfill",
                    CirclesFulfillmentTrigger = "confirmed"
                }
            }
        };

        _ordersMock.Setup(x => x.GetByPaymentReference(payRef))
            .Returns(new[] { (orderId, (string?)"0xbuyer", (long?)100) });
        _ordersMock.Setup(x => x.Get(orderId)).Returns(snapshot);

        // Act
        await _sut.OnConfirmedAsync(payRef, DateTimeOffset.Now);

        // Assert
        _fulfillmentMock.Verify(x => x.FulfillAsync(
            "https://example.com/fulfill", orderId, payRef, snapshot, "confirmed", It.IsAny<CancellationToken>()),
            Times.Once);

        // Act
        await _sut.OnFinalizedAsync(payRef, DateTimeOffset.Now);

        // Assert
        _fulfillmentMock.Verify(x => x.FulfillAsync(
            "https://example.com/fulfill", orderId, payRef, snapshot, "finalized", It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
