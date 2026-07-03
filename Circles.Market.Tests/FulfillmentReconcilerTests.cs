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
    private const string OrderIdsEnv = "FULFILLMENT_RECONCILE_ORDER_IDS";
    private const string SettledAfterEnv = "FULFILLMENT_RECONCILE_SETTLED_AFTER";

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
        foreach (var key in new[] { EnabledEnv, GraceEnv, MaxAttemptsEnv, LimitEnv, OrderIdsEnv, SettledAfterEnv })
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    // Env is read in the reconciler ctor, so it must be set before construction.
    // An ACTIVE reconciler requires scoping, so enabled runs get a settled-after cutoff
    // unless the test configures its own scope beforehand.
    private FulfillmentReconciler MakeReconciler(bool enabled)
    {
        Environment.SetEnvironmentVariable(EnabledEnv, enabled ? "true" : "false");
        if (enabled &&
            Environment.GetEnvironmentVariable(OrderIdsEnv) is null &&
            Environment.GetEnvironmentVariable(SettledAfterEnv) is null)
        {
            Environment.SetEnvironmentVariable(SettledAfterEnv, "2026-01-01T00:00:00Z");
        }
        return new FulfillmentReconciler(
            _source.Object, _hooks.Object, _orders.Object, NullLogger<FulfillmentReconciler>.Instance);
    }

    private void SetupCandidates(params StrandedOrder[] candidates)
    {
        _source.Setup(s => s.GetStrandedAsync(It.IsAny<StrandedQuery>(), It.IsAny<CancellationToken>()))
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

    // ── Safety scoping ───────────────────────────────────────────────────────

    [Test]
    public void Enabled_WithoutAnyScope_RefusesToConstruct()
    {
        // The unscoped candidate set includes orders fulfilled before success markers
        // existed — an active reconciler must never run against it.
        Environment.SetEnvironmentVariable(EnabledEnv, "true");

        Assert.Throws<InvalidOperationException>(() => new FulfillmentReconciler(
            _source.Object, _hooks.Object, _orders.Object, NullLogger<FulfillmentReconciler>.Instance));
    }

    [Test]
    public async Task Shadow_WithoutScope_StillRuns()
    {
        SetupCandidates(new StrandedOrder("ord_1", "pay_1"));
        var sut = MakeReconciler(enabled: false);

        var result = await sut.ReconcileOnceAsync(CancellationToken.None);

        Assert.That(result.Candidates, Is.EqualTo(1));
        Assert.That(result.Enabled, Is.False);
    }

    [Test]
    public async Task OrderIdAllowlist_IsParsedAndPassedToQuery()
    {
        Environment.SetEnvironmentVariable(OrderIdsEnv, " ord_A, ord_B,,ord_A \n ord_C ");
        SetupCandidates();
        StrandedQuery? seen = null;
        _source.Setup(s => s.GetStrandedAsync(It.IsAny<StrandedQuery>(), It.IsAny<CancellationToken>()))
            .Callback<StrandedQuery, CancellationToken>((q, _) => seen = q)
            .ReturnsAsync(Array.Empty<StrandedOrder>());
        var sut = MakeReconciler(enabled: true);

        await sut.ReconcileOnceAsync(CancellationToken.None);

        Assert.That(seen, Is.Not.Null);
        Assert.That(seen!.OrderIds, Is.EquivalentTo(new[] { "ord_A", "ord_B", "ord_C" }),
            "csv parsing must trim, drop empties, and dedupe");
        Assert.That(seen.SettledAfter, Is.Null, "allowlist-only scope must not gain a cutoff");
    }

    [Test]
    public async Task SettledAfterCutoff_IsParsedAndPassedToQuery()
    {
        Environment.SetEnvironmentVariable(SettledAfterEnv, "2026-07-03T10:00:00Z");
        SetupCandidates();
        StrandedQuery? seen = null;
        _source.Setup(s => s.GetStrandedAsync(It.IsAny<StrandedQuery>(), It.IsAny<CancellationToken>()))
            .Callback<StrandedQuery, CancellationToken>((q, _) => seen = q)
            .ReturnsAsync(Array.Empty<StrandedOrder>());
        var sut = MakeReconciler(enabled: true);

        await sut.ReconcileOnceAsync(CancellationToken.None);

        Assert.That(seen, Is.Not.Null);
        Assert.That(seen!.SettledAfter, Is.EqualTo(DateTimeOffset.Parse("2026-07-03T10:00:00Z")));
        Assert.That(seen.OrderIds, Is.Null);
    }

    [Test]
    public void InvalidSettledAfter_Throws_EvenInShadow()
    {
        // A typo'd safety cutoff must be loud, not silently ignored.
        Environment.SetEnvironmentVariable(SettledAfterEnv, "not-a-date");
        Environment.SetEnvironmentVariable(EnabledEnv, "false");

        Assert.Throws<InvalidOperationException>(() => new FulfillmentReconciler(
            _source.Object, _hooks.Object, _orders.Object, NullLogger<FulfillmentReconciler>.Instance));
    }

    [Test]
    public void DegenerateOrderIds_SetButEmpty_Throws_EvenWithCutoffPresent()
    {
        // A set-but-id-less allowlist must not silently degrade to cutoff-only scope.
        Environment.SetEnvironmentVariable(OrderIdsEnv, " , ,, ");
        Environment.SetEnvironmentVariable(SettledAfterEnv, "2026-07-01T00:00:00Z");
        Environment.SetEnvironmentVariable(EnabledEnv, "true");

        Assert.Throws<InvalidOperationException>(() => new FulfillmentReconciler(
            _source.Object, _hooks.Object, _orders.Object, NullLogger<FulfillmentReconciler>.Instance));
    }

    [Test]
    public void AmbiguousDateFormat_IsRejected()
    {
        // Lenient parsing would read 03/07/2026 as March 7 — a silently mis-set cutoff.
        Environment.SetEnvironmentVariable(SettledAfterEnv, "03/07/2026");
        Environment.SetEnvironmentVariable(EnabledEnv, "false");

        Assert.Throws<InvalidOperationException>(() => new FulfillmentReconciler(
            _source.Object, _hooks.Object, _orders.Object, NullLogger<FulfillmentReconciler>.Instance));
    }

    [Test]
    public void EmptyStringOrderIds_CountsAsUnset_GuardStillFires()
    {
        // Empty string is the templated-unset shape — treated as absent, so the
        // enabled-unscoped guard must reject construction.
        Environment.SetEnvironmentVariable(OrderIdsEnv, "");
        Environment.SetEnvironmentVariable(EnabledEnv, "true");

        Assert.Throws<InvalidOperationException>(() => new FulfillmentReconciler(
            _source.Object, _hooks.Object, _orders.Object, NullLogger<FulfillmentReconciler>.Instance));
    }
}
