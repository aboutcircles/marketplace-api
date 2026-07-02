using System.Text.Json;
using Circles.Market.Api.Cart;
using Circles.Market.Api.Payments;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Circles.Market.Tests;

[TestFixture]
public class FulfillmentReconcilerTests
{
    private const string EnabledEnv = "FULFILLMENT_RECONCILE_ENABLED";
    private const string GraceEnv = "FULFILLMENT_RECONCILE_GRACE_MINUTES";
    private const string MaxAttemptsEnv = "FULFILLMENT_RECONCILE_MAX_ATTEMPTS";
    private const string LimitEnv = "FULFILLMENT_RECONCILE_LIMIT";

    private Mock<IStrandedFulfillmentSource> _source = null!;
    private Mock<IOrderLifecycleHooks> _hooks = null!;
    private Mock<IOrderStore> _orders = null!;

    [SetUp]
    public void SetUp()
    {
        _source = new Mock<IStrandedFulfillmentSource>();
        _hooks = new Mock<IOrderLifecycleHooks>();
        _orders = new Mock<IOrderStore>();
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var key in new[] { EnabledEnv, GraceEnv, MaxAttemptsEnv, LimitEnv })
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    // Env is read in the reconciler ctor, so it must be set before construction.
    private FulfillmentReconciler MakeReconciler(bool enabled)
    {
        Environment.SetEnvironmentVariable(EnabledEnv, enabled ? "true" : "false");
        return new FulfillmentReconciler(
            _source.Object, _hooks.Object, _orders.Object, NullLogger<FulfillmentReconciler>.Instance);
    }

    private void SetupCandidates(params StrandedOrder[] candidates)
    {
        _source.Setup(s => s.GetStrandedAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(candidates);
    }

    private static OrderOutboxItem FulfillmentOutbox(string orderId) =>
        new(1, orderId, "fulfillment", DateTimeOffset.UtcNow, default);

    [Test]
    public async Task ShadowMode_DetectsButDoesNotReDrive()
    {
        SetupCandidates(new StrandedOrder("ord_1", "pay_1"), new StrandedOrder("ord_2", "pay_2"));
        var sut = MakeReconciler(enabled: false);

        var result = await sut.ReconcileOnceAsync(CancellationToken.None);

        Assert.That(result.Enabled, Is.False);
        Assert.That(result.Candidates, Is.EqualTo(2));
        Assert.That(result.Reconciled, Is.EqualTo(0));
        Assert.That(result.Failed, Is.EqualTo(0));

        // Shadow mode fires nothing and writes no markers.
        _hooks.Verify(h => h.OnConfirmedAsync(It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
        _hooks.Verify(h => h.OnFinalizedAsync(It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
        _orders.Verify(o => o.AddOutboxItem(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<JsonElement>()), Times.Never);
    }

    [Test]
    public async Task Active_ReDrivesBothTriggers_AndCountsRecoveredWhenFulfillmentAppears()
    {
        SetupCandidates(new StrandedOrder("ord_1", "pay_1"));
        // After re-drive, a 'fulfillment' outbox row exists -> success signal.
        _orders.Setup(o => o.GetOutboxItems("ord_1")).Returns(new[] { FulfillmentOutbox("ord_1") });
        var sut = MakeReconciler(enabled: true);

        var result = await sut.ReconcileOnceAsync(CancellationToken.None);

        Assert.That(result.Enabled, Is.True);
        Assert.That(result.Candidates, Is.EqualTo(1));
        Assert.That(result.Reconciled, Is.EqualTo(1));
        Assert.That(result.Failed, Is.EqualTo(0));

        _hooks.Verify(h => h.OnConfirmedAsync("pay_1", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
        _hooks.Verify(h => h.OnFinalizedAsync("pay_1", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
        // Recovered orders get no attempt marker.
        _orders.Verify(o => o.AddOutboxItem(It.IsAny<string>(), "fulfillment_reconcile", It.IsAny<JsonElement>()), Times.Never);
    }

    [Test]
    public async Task Active_StillStranded_RecordsAttemptMarkerAndCountsFailed()
    {
        SetupCandidates(new StrandedOrder("ord_1", "pay_1"));
        // No 'fulfillment' outbox row after re-drive -> still stranded.
        _orders.Setup(o => o.GetOutboxItems("ord_1")).Returns(Array.Empty<OrderOutboxItem>());
        var sut = MakeReconciler(enabled: true);

        var result = await sut.ReconcileOnceAsync(CancellationToken.None);

        Assert.That(result.Reconciled, Is.EqualTo(0));
        Assert.That(result.Failed, Is.EqualTo(1));

        // The bounded-retry marker is written so route-less/broken orders stop being re-selected at the cap.
        _orders.Verify(o => o.AddOutboxItem("ord_1", "fulfillment_reconcile", It.IsAny<JsonElement>()), Times.Once);
    }

    [Test]
    public async Task Active_HookThrows_IsCaught_AndCountsFailedWithMarker()
    {
        SetupCandidates(new StrandedOrder("ord_1", "pay_1"));
        _hooks.Setup(h => h.OnFinalizedAsync("pay_1", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("adapter down"));
        var sut = MakeReconciler(enabled: true);

        var result = await sut.ReconcileOnceAsync(CancellationToken.None);

        Assert.That(result.Failed, Is.EqualTo(1));
        Assert.That(result.Reconciled, Is.EqualTo(0));
        _orders.Verify(o => o.AddOutboxItem("ord_1", "fulfillment_reconcile", It.IsAny<JsonElement>()), Times.Once);
    }

    [Test]
    public async Task Active_DedupesByPaymentReference()
    {
        // Two orders share one payment reference; the hooks fan out per reference, so re-drive once.
        SetupCandidates(new StrandedOrder("ord_1", "pay_shared"), new StrandedOrder("ord_2", "pay_shared"));
        _orders.Setup(o => o.GetOutboxItems(It.IsAny<string>())).Returns(new[] { FulfillmentOutbox("ord_1") });
        var sut = MakeReconciler(enabled: true);

        var result = await sut.ReconcileOnceAsync(CancellationToken.None);

        Assert.That(result.Candidates, Is.EqualTo(1), "shared reference collapses to one re-drive");
        _hooks.Verify(h => h.OnFinalizedAsync("pay_shared", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task NoCandidates_IsNoOp()
    {
        SetupCandidates();
        var sut = MakeReconciler(enabled: true);

        var result = await sut.ReconcileOnceAsync(CancellationToken.None);

        Assert.That(result.Candidates, Is.EqualTo(0));
        _hooks.Verify(h => h.OnFinalizedAsync(It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
