using System.Numerics;
using Circles.Market.Api;
using Circles.Market.Api.Cart;
using Circles.Market.Api.Metrics;
using Circles.Market.Api.Payments;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Circles.Market.Tests;

/// <summary>
/// State-machine tests for <see cref="OrderPaymentFlow"/>: observed → paid → confirmed →
/// finalized transitions, no-op paths, eligibility trace emission, and isolation of
/// fire-and-forget lifecycle hooks (a failing hook must not affect the settlement result).
/// Hooks run on background tasks, so assertions on them are gated by semaphore signals
/// rather than delays.
/// </summary>
[TestFixture]
public class OrderPaymentFlowTests
{
    private const long ChainId = 100;
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private Mock<IPaymentStore> _payments = null!;
    private Mock<IOrderStore> _orders = null!;
    private Mock<IOrderLifecycleHooks> _hooks = null!;
    private Mock<IOrderProcessingTraceSink> _trace = null!;
    private List<OrderProcessingTraceEvent> _traceEvents = null!;
    private OrderPaymentFlow _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _payments = new Mock<IPaymentStore>();
        _orders = new Mock<IOrderStore>();
        _hooks = new Mock<IOrderLifecycleHooks>();
        _trace = new Mock<IOrderProcessingTraceSink>();
        _traceEvents = new List<OrderProcessingTraceEvent>();
        _trace.Setup(t => t.Emit(It.IsAny<OrderProcessingTraceEvent>()))
            .Callback<OrderProcessingTraceEvent>(e => { lock (_traceEvents) _traceEvents.Add(e); });

        _sut = new OrderPaymentFlow(
            _payments.Object,
            _orders.Object,
            _hooks.Object,
            _trace.Object,
            NullLogger<OrderPaymentFlow>.Instance);
    }

    private static PaymentTransferRecord Transfer(
        string payRef, bool? eligible = true, BigInteger? amountWei = null) => new(
        ChainId: ChainId,
        TxHash: "0xtx",
        LogIndex: 0,
        TransactionIndex: 0,
        BlockNumber: 1,
        PaymentReference: payRef,
        GatewayAddress: "0xgateway",
        PayerAddress: "0xpayer",
        AmountWei: amountWei ?? new BigInteger(5_000_000_000_000_000_000UL),
        CreatedAt: Now,
        TokenId: BigInteger.One,
        TokenAvatar: "0xavatar",
        Eligible: eligible);

    private static PaymentRecord Payment(string payRef) =>
        Payment(payRef, new BigInteger(5_000_000_000_000_000_000UL));

    // total: null mirrors production's SUM(...) FILTER (WHERE eligible IS TRUE) yielding NULL
    // when a reference has no eligible transfers — the state ineligible/undetermined tests model.
    private static PaymentRecord Payment(string payRef, BigInteger? total) => new(
        ChainId: ChainId,
        PaymentReference: payRef,
        GatewayAddress: "0xgateway",
        PayerAddress: "0xpayer",
        TotalAmountWei: total,
        Status: "observed",
        CreatedAt: Now,
        ConfirmedAt: null,
        FinalizedAt: null,
        FirstBlockNumber: 1,
        FirstTxHash: "0xtx",
        FirstLogIndex: 0,
        LastBlockNumber: 1,
        LastTxHash: "0xtx",
        LastLogIndex: 0);

    private bool TraceSeen(string stage)
    {
        lock (_traceEvents) return _traceEvents.Any(e => e.Stage == stage);
    }

    private static async Task WaitSignalAsync(SemaphoreSlim signal, string what)
    {
        Assert.That(await signal.WaitAsync(TimeSpan.FromSeconds(5)), Is.True,
            $"timed out waiting for {what}");
    }

    // ── Observed ────────────────────────────────────────────────────────────

    [Test]
    public void HandleObservedTransferAsync_NullTransfer_Throws()
    {
        Assert.ThrowsAsync<ArgumentNullException>(() => _sut.HandleObservedTransferAsync(null!));
    }

    [Test]
    public async Task Observed_MarksPaid_AndFiresPaidHooks()
    {
        const string payRef = "pay_OBS_OK";
        _payments.Setup(p => p.UpsertAndGetPayment(ChainId, payRef)).Returns(Payment(payRef));
        _orders.Setup(o => o.TryMarkPaidByReference(
                payRef, ChainId, "0xtx", 0, "0xgateway", It.IsAny<BigInteger?>(), It.IsAny<DateTimeOffset>()))
            .Returns(true);

        using var paid = new SemaphoreSlim(0);
        using var statusChanged = new SemaphoreSlim(0);
        _hooks.Setup(h => h.OnPaidAsync(payRef, ChainId, "0xtx", 0, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask).Callback(() => paid.Release());
        _hooks.Setup(h => h.OnStatusChangedAsync(payRef, null, StatusUris.PaymentProcessing,
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask).Callback(() => statusChanged.Release());

        bool changed = await _sut.HandleObservedTransferAsync(Transfer(payRef));

        Assert.That(changed, Is.True);
        _payments.Verify(p => p.UpsertObservedTransfer(It.Is<PaymentTransferRecord>(t => t.PaymentReference == payRef)), Times.Once);
        await WaitSignalAsync(paid, "OnPaidAsync");
        await WaitSignalAsync(statusChanged, "OnStatusChangedAsync(PaymentProcessing)");
        Assert.That(TraceSeen("order_marked_paid"), Is.True);
    }

    [Test]
    public async Task Observed_NoStateChange_WhenOrderNotMarked()
    {
        // e.g. no matching order, or the SQL amount gate rejected the total
        const string payRef = "pay_OBS_NOMATCH";
        _payments.Setup(p => p.UpsertAndGetPayment(ChainId, payRef)).Returns(Payment(payRef));
        _orders.Setup(o => o.TryMarkPaidByReference(
                It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<string>(), It.IsAny<BigInteger?>(), It.IsAny<DateTimeOffset>()))
            .Returns(false);

        bool changed = await _sut.HandleObservedTransferAsync(Transfer(payRef));

        Assert.That(changed, Is.False);
        _hooks.VerifyNoOtherCalls();
        Assert.That(TraceSeen("order_marked_paid"), Is.False);
    }

    [Test]
    public async Task Observed_ReturnsFalse_WhenAggregationYieldsNothing()
    {
        const string payRef = "pay_OBS_NOAGG";
        _payments.Setup(p => p.UpsertAndGetPayment(ChainId, payRef)).Returns((PaymentRecord?)null);

        bool changed = await _sut.HandleObservedTransferAsync(Transfer(payRef));

        Assert.That(changed, Is.False);
        _orders.Verify(o => o.TryMarkPaidByReference(
            It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<string>(), It.IsAny<BigInteger?>(), It.IsAny<DateTimeOffset>()), Times.Never);
    }

    [Test]
    public async Task Observed_IneligibleTransfer_EmitsWarnTrace_AndMetric()
    {
        const string payRef = "pay_OBS_INEL";
        double before = MarketplaceMetrics.PaymentsIneligibleToken.Value;
        _payments.Setup(p => p.UpsertAndGetPayment(ChainId, payRef)).Returns(Payment(payRef, total: null));

        await _sut.HandleObservedTransferAsync(Transfer(payRef, eligible: false));

        Assert.That(MarketplaceMetrics.PaymentsIneligibleToken.Value, Is.EqualTo(before + 1));
        Assert.That(TraceSeen("payment_ineligible_token"), Is.True);
        // Recorded regardless of eligibility — crediting is the aggregate's job.
        _payments.Verify(p => p.UpsertObservedTransfer(It.IsAny<PaymentTransferRecord>()), Times.Once);
    }

    [Test]
    public async Task Observed_UndeterminedTransfer_EmitsWarnTrace_AndMetric()
    {
        const string payRef = "pay_OBS_UNDET";
        double before = MarketplaceMetrics.PaymentsUndeterminedToken.Value;
        _payments.Setup(p => p.UpsertAndGetPayment(ChainId, payRef)).Returns(Payment(payRef, total: null));

        await _sut.HandleObservedTransferAsync(Transfer(payRef, eligible: null));

        Assert.That(MarketplaceMetrics.PaymentsUndeterminedToken.Value, Is.EqualTo(before + 1));
        Assert.That(TraceSeen("payment_undetermined_token"), Is.True);
    }

    [Test]
    public async Task Observed_MissingPaymentReferenceOnAggregate_ReturnsFalse_WithTrace()
    {
        const string payRef = "pay_OBS_EMPTYREF";
        _payments.Setup(p => p.UpsertAndGetPayment(ChainId, payRef))
            .Returns(Payment(payRef) with { PaymentReference = "" });

        bool changed = await _sut.HandleObservedTransferAsync(Transfer(payRef));

        Assert.That(changed, Is.False);
        Assert.That(TraceSeen("payment_aggregated"), Is.True);
        _orders.Verify(o => o.TryMarkPaidByReference(
            It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<string>(), It.IsAny<BigInteger?>(), It.IsAny<DateTimeOffset>()), Times.Never);
    }

    [Test]
    public async Task Observed_HookFailure_DoesNotAffectResult_AndIsTraced()
    {
        const string payRef = "pay_OBS_HOOKFAIL";
        _payments.Setup(p => p.UpsertAndGetPayment(ChainId, payRef)).Returns(Payment(payRef));
        _orders.Setup(o => o.TryMarkPaidByReference(
                It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<string>(), It.IsAny<BigInteger?>(), It.IsAny<DateTimeOffset>()))
            .Returns(true);
        _hooks.Setup(h => h.OnPaidAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("adapter down"));

        using var failed = new SemaphoreSlim(0);
        _trace.Setup(t => t.Emit(It.Is<OrderProcessingTraceEvent>(e => e.Stage == "hooks_paid_failed")))
            .Callback(() => failed.Release());

        bool changed = await _sut.HandleObservedTransferAsync(Transfer(payRef));

        Assert.That(changed, Is.True, "settlement result must not depend on hook outcome");
        await WaitSignalAsync(failed, "hooks_paid_failed trace");
    }

    // ── Confirmed ───────────────────────────────────────────────────────────

    [Test]
    public async Task Confirmed_MarksOrder_AndFiresHook()
    {
        const string payRef = "pay_CONF_OK";
        _payments.Setup(p => p.MarkConfirmed(ChainId, payRef, 42, Now)).Returns(true);
        _payments.Setup(p => p.GetPayment(ChainId, payRef)).Returns(Payment(payRef));
        _orders.Setup(o => o.TryMarkConfirmedByReference(payRef, Now)).Returns(true);

        using var confirmed = new SemaphoreSlim(0);
        _hooks.Setup(h => h.OnConfirmedAsync(payRef, Now, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask).Callback(() => confirmed.Release());

        await _sut.HandleConfirmedAsync(ChainId, payRef, 42, Now);

        _orders.Verify(o => o.TryMarkConfirmedByReference(payRef, Now), Times.Once);
        await WaitSignalAsync(confirmed, "OnConfirmedAsync");
        Assert.That(TraceSeen("order_marked_confirmed"), Is.True);
    }

    [Test]
    public async Task Confirmed_NoOp_WhenPaymentAlreadyConfirmed()
    {
        const string payRef = "pay_CONF_NOOP";
        _payments.Setup(p => p.MarkConfirmed(ChainId, payRef, 42, Now)).Returns(false);

        await _sut.HandleConfirmedAsync(ChainId, payRef, 42, Now);

        _payments.Verify(p => p.GetPayment(It.IsAny<long>(), It.IsAny<string>()), Times.Never);
        _orders.Verify(o => o.TryMarkConfirmedByReference(It.IsAny<string>(), It.IsAny<DateTimeOffset>()), Times.Never);
        Assert.That(TraceSeen("payment_confirm_ignored"), Is.True);
    }

    [Test]
    public async Task Confirmed_NoHook_WhenOrderNotUpdated()
    {
        const string payRef = "pay_CONF_NOORD";
        _payments.Setup(p => p.MarkConfirmed(ChainId, payRef, 42, Now)).Returns(true);
        _payments.Setup(p => p.GetPayment(ChainId, payRef)).Returns(Payment(payRef));
        _orders.Setup(o => o.TryMarkConfirmedByReference(payRef, Now)).Returns(false);

        await _sut.HandleConfirmedAsync(ChainId, payRef, 42, Now);

        _hooks.Verify(h => h.OnConfirmedAsync(It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Finalized ───────────────────────────────────────────────────────────

    [Test]
    public async Task Finalized_MarksOrder_AndFiresFinalizedHooks()
    {
        const string payRef = "pay_FIN_OK";
        _payments.Setup(p => p.MarkFinalized(ChainId, payRef, Now)).Returns(true);
        _payments.Setup(p => p.GetPayment(ChainId, payRef)).Returns(Payment(payRef));
        _orders.Setup(o => o.TryMarkFinalizedByReference(payRef, Now)).Returns(true);
        _orders.Setup(o => o.GetByPaymentReference(payRef))
            .Returns(Array.Empty<(string, string?, long?)>());

        using var finalized = new SemaphoreSlim(0);
        using var statusChanged = new SemaphoreSlim(0);
        _hooks.Setup(h => h.OnFinalizedAsync(payRef, Now, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask).Callback(() => finalized.Release());
        _hooks.Setup(h => h.OnStatusChangedAsync(payRef, StatusUris.PaymentProcessing, StatusUris.PaymentComplete,
                Now, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask).Callback(() => statusChanged.Release());

        await _sut.HandleFinalizationAsync(ChainId, payRef, Now);

        _orders.Verify(o => o.TryMarkFinalizedByReference(payRef, Now), Times.Once);
        await WaitSignalAsync(finalized, "OnFinalizedAsync");
        await WaitSignalAsync(statusChanged, "OnStatusChangedAsync(PaymentComplete)");
        Assert.That(TraceSeen("order_marked_finalized"), Is.True);
    }

    [Test]
    public async Task Finalized_NoOp_WhenPaymentAlreadyFinalized()
    {
        const string payRef = "pay_FIN_NOOP";
        _payments.Setup(p => p.MarkFinalized(ChainId, payRef, Now)).Returns(false);

        await _sut.HandleFinalizationAsync(ChainId, payRef, Now);

        _orders.Verify(o => o.TryMarkFinalizedByReference(It.IsAny<string>(), It.IsAny<DateTimeOffset>()), Times.Never);
        Assert.That(TraceSeen("payment_finalize_ignored"), Is.True);
    }

    [Test]
    public async Task Finalized_HookFailure_IsTraced_AndDoesNotThrow()
    {
        const string payRef = "pay_FIN_HOOKFAIL";
        _payments.Setup(p => p.MarkFinalized(ChainId, payRef, Now)).Returns(true);
        _payments.Setup(p => p.GetPayment(ChainId, payRef)).Returns(Payment(payRef));
        _orders.Setup(o => o.TryMarkFinalizedByReference(payRef, Now)).Returns(true);
        _orders.Setup(o => o.GetByPaymentReference(payRef))
            .Returns(Array.Empty<(string, string?, long?)>());
        _hooks.Setup(h => h.OnFinalizedAsync(It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("adapter down"));

        using var failed = new SemaphoreSlim(0);
        _trace.Setup(t => t.Emit(It.Is<OrderProcessingTraceEvent>(e => e.Stage == "hooks_finalized_failed")))
            .Callback(() => failed.Release());

        await _sut.HandleFinalizationAsync(ChainId, payRef, Now);

        await WaitSignalAsync(failed, "hooks_finalized_failed trace");
    }
}
