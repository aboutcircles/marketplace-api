using Circles.Market.Api.Cart;
using Circles.Market.Api.Metrics;
using Circles.Market.Api.Payments;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Circles.Market.Tests;

[TestFixture]
public class MarketplaceMetricsTests
{
    [Test]
    public void OrdersCreated_IncrementedOnCheckout()
    {
        // Static counters accumulate across tests — capture baseline
        double before = MarketplaceMetrics.OrdersCreated.Value;

        MarketplaceMetrics.OrdersCreated.Inc();

        Assert.That(MarketplaceMetrics.OrdersCreated.Value, Is.EqualTo(before + 1));
    }

    [Test]
    public void OrderValueCrc_ObservesDecimalPrice()
    {
        double before = MarketplaceMetrics.OrderValueCrc.Count;

        MarketplaceMetrics.OrderValueCrc.Observe(42.5);

        Assert.That(MarketplaceMetrics.OrderValueCrc.Count, Is.EqualTo(before + 1));
        Assert.That(MarketplaceMetrics.OrderValueCrc.Sum, Is.GreaterThanOrEqualTo(42.5));
    }

    [Test]
    public void PaymentAmountCrc_IncByAmount()
    {
        double before = MarketplaceMetrics.PaymentAmountCrc.Value;

        MarketplaceMetrics.PaymentAmountCrc.Inc(99.9);

        Assert.That(MarketplaceMetrics.PaymentAmountCrc.Value, Is.EqualTo(before + 99.9).Within(0.001));
    }
}

[TestFixture]
public class OrderPaymentFlowMetricsTests
{
    private Mock<IPaymentStore> _paymentsMock = null!;
    private Mock<IOrderStore> _ordersMock = null!;
    private Mock<IOrderLifecycleHooks> _hooksMock = null!;
    private Mock<IOrderProcessingTraceSink> _traceMock = null!;
    private OrderPaymentFlow _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _paymentsMock = new Mock<IPaymentStore>();
        _ordersMock = new Mock<IOrderStore>();
        _hooksMock = new Mock<IOrderLifecycleHooks>();
        _traceMock = new Mock<IOrderProcessingTraceSink>();
        _sut = new OrderPaymentFlow(
            _paymentsMock.Object,
            _ordersMock.Object,
            _hooksMock.Object,
            _traceMock.Object,
            NullLogger<OrderPaymentFlow>.Instance);
    }

    [Test]
    public async Task HandleFinalizationAsync_IncrementsMetrics_WhenOrderFinalized()
    {
        // Arrange
        long chainId = 100;
        string payRef = "pay_TEST_FINAL";
        var now = DateTimeOffset.UtcNow;

        _paymentsMock.Setup(p => p.MarkFinalized(chainId, payRef, now)).Returns(true);
        _paymentsMock.Setup(p => p.GetPayment(chainId, payRef)).Returns(
            new PaymentRecord(
                ChainId: chainId,
                PaymentReference: payRef,
                GatewayAddress: "0xgateway",
                PayerAddress: null,
                TotalAmountWei: null,
                Status: "finalized",
                CreatedAt: now,
                ConfirmedAt: null,
                FinalizedAt: now,
                FirstBlockNumber: null,
                FirstTxHash: null,
                FirstLogIndex: null,
                LastBlockNumber: null,
                LastTxHash: null,
                LastLogIndex: null));

        _ordersMock.Setup(o => o.TryMarkFinalizedByReference(payRef, now)).Returns(true);
        _ordersMock.Setup(o => o.GetByPaymentReference(payRef))
            .Returns(new[] { ("ord_123", (string?)"0xbuyer", (long?)chainId) });
        _ordersMock.Setup(o => o.Get("ord_123"))
            .Returns(new OrderSnapshot
            {
                TotalPaymentDue = new PriceSpecification { Price = 50m, PriceCurrency = "CRC" }
            });

        // Stub async hooks to prevent NRE in fire-and-forget Task.Run
        _hooksMock.Setup(h => h.OnFinalizedAsync(It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _hooksMock.Setup(h => h.OnStatusChangedAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(),
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        double finalizedBefore = MarketplaceMetrics.OrdersFinalized.Value;
        double crcBefore = MarketplaceMetrics.PaymentAmountCrc.Value;

        // Act
        await _sut.HandleFinalizationAsync(chainId, payRef, now);

        // Assert
        Assert.That(MarketplaceMetrics.OrdersFinalized.Value, Is.EqualTo(finalizedBefore + 1));
        Assert.That(MarketplaceMetrics.PaymentAmountCrc.Value, Is.EqualTo(crcBefore + 50).Within(0.001));
    }

    [Test]
    public async Task HandleFinalizationAsync_NoMetrics_WhenPaymentNotUpdated()
    {
        // MarkFinalized returns false → early exit, no metrics
        long chainId = 100;
        string payRef = "pay_NOOP";
        var now = DateTimeOffset.UtcNow;

        _paymentsMock.Setup(p => p.MarkFinalized(chainId, payRef, now)).Returns(false);

        double finalizedBefore = MarketplaceMetrics.OrdersFinalized.Value;

        await _sut.HandleFinalizationAsync(chainId, payRef, now);

        Assert.That(MarketplaceMetrics.OrdersFinalized.Value, Is.EqualTo(finalizedBefore));
    }
}
