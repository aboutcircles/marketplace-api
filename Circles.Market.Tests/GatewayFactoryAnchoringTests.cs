using Circles.Market.Api.Payments;

namespace Circles.Market.Tests;

/// <summary>
/// Factory-anchored trust reduction and verdict selection: only TrustUpdated rows emitted by
/// the canonical gateway factory count toward the anchored set — a forged row from any other
/// emitter can neither grant trust nor, by being newer, revoke trust the factory established.
/// Shadow mode reports divergence without changing the effective verdict.
/// </summary>
[TestFixture]
public class GatewayFactoryAnchoringTests
{
    private const string Factory = "0x186725d8fe10a573dc73144f7a317fcae5314f19";
    private const string Attacker = "0xbadbadbadbadbadbadbadbadbadbadbadbadbad0";
    private static readonly string[] FactoryFilter = [Factory];

    private static readonly string[] Cols =
        { "trustReceiver", "expiry", "blockNumber", "transactionIndex", "logIndex", "emitter" };

    private const long Now = 1_000_000;
    private const long Future = 2_000_000;
    private const long Past = 500_000;

    // ── Anchored-set reduction ───────────────────────────────────────────────

    [Test]
    public void ForgedRow_GrantsNothing_InAnchoredSet()
    {
        object[][] rows =
        {
            new object[] { "0xAAA", Future, 10, 0, 0, Attacker }
        };

        var anchored = CirclesPaymentsPoller.ParseActiveTrustSet(Cols, rows, Now, FactoryFilter);
        var all = CirclesPaymentsPoller.ParseActiveTrustSet(Cols, rows, Now);

        Assert.That(anchored, Is.Empty, "attacker-emitted trust must not count");
        Assert.That(all, Is.EquivalentTo(new[] { "0xaaa" }), "legacy set still sees it (shadow comparison)");
    }

    [Test]
    public void ForgedNewerRevocation_CannotRevoke_FactoryTrust()
    {
        object[][] rows =
        {
            new object[] { "0xAAA", Future, 10, 0, 0, Factory },
            new object[] { "0xAAA", Past, 20, 0, 0, Attacker } // newer forged untrust
        };

        var anchored = CirclesPaymentsPoller.ParseActiveTrustSet(Cols, rows, Now, FactoryFilter);
        var all = CirclesPaymentsPoller.ParseActiveTrustSet(Cols, rows, Now);

        Assert.That(anchored, Is.EquivalentTo(new[] { "0xaaa" }),
            "forged rows are discarded BEFORE latest-wins — they cannot untrust-DoS a legit token");
        Assert.That(all, Is.Empty, "legacy reduction is vulnerable to the forged revocation");
    }

    [Test]
    public void FactoryRows_ReduceNormally_InAnchoredSet()
    {
        object[][] rows =
        {
            new object[] { "0xAAA", Future, 10, 0, 0, Factory },
            new object[] { "0xAAA", Past, 20, 0, 0, Factory }, // genuine later revocation
            new object[] { "0xBBB", Future, 15, 0, 0, Factory }
        };

        var anchored = CirclesPaymentsPoller.ParseActiveTrustSet(Cols, rows, Now, FactoryFilter);

        Assert.That(anchored, Is.EquivalentTo(new[] { "0xbbb" }),
            "factory-emitted revocations still apply");
    }

    [Test]
    public void EmitterCase_IsNormalized()
    {
        object[][] rows =
        {
            new object[] { "0xAAA", Future, 10, 0, 0, "0x186725D8FE10A573DC73144F7A317FCAE5314F19" }
        };

        var anchored = CirclesPaymentsPoller.ParseActiveTrustSet(Cols, rows, Now, FactoryFilter);

        Assert.That(anchored, Is.EquivalentTo(new[] { "0xaaa" }));
    }

    [Test]
    public void MissingEmitterColumn_WithFilter_YieldsEmptySet_FailSafe()
    {
        string[] colsNoEmitter = { "trustReceiver", "expiry", "blockNumber", "transactionIndex", "logIndex" };
        object[][] rows =
        {
            new object[] { "0xAAA", Future, 10, 0, 0 }
        };

        var anchored = CirclesPaymentsPoller.ParseActiveTrustSet(colsNoEmitter, rows, Now, FactoryFilter);

        Assert.That(anchored, Is.Empty, "unattributable rows must not grant anchored trust");
    }

    // ── Verdict selection ────────────────────────────────────────────────────

    private static GatewayTrustSets Sets(string[] all, string[]? anchored) =>
        new(new HashSet<string>(all), anchored is null ? null : new HashSet<string>(anchored));

    [Test]
    public void Enforced_UsesAnchoredSetOnly_AndStillReportsDivergence()
    {
        var sets = Sets(all: ["0xaaa"], anchored: []);

        var (verdict, diverged) = CirclesPaymentsPoller.DecideFactoryAwareEligibility(
            sets, "0xaaa", factoryConfigured: true, enforced: true);

        Assert.That(verdict, Is.False, "legacy-only trust must not credit under enforcement");
        Assert.That(diverged, Is.True,
            "enforced divergence is the signal distinguishing blocked-forged-trust / broken-legit-gateway from ordinary rejections");
    }

    [Test]
    public void Enforced_AgreeingVerdicts_NoDivergence()
    {
        var sets = Sets(all: ["0xaaa"], anchored: ["0xaaa"]);

        var (verdict, diverged) = CirclesPaymentsPoller.DecideFactoryAwareEligibility(
            sets, "0xaaa", factoryConfigured: true, enforced: true);

        Assert.That(verdict, Is.True);
        Assert.That(diverged, Is.False);
    }

    [Test]
    public void Shadow_AnchoredUnavailable_IsNotDivergence()
    {
        // Shadow-mode degradation (anchored fetch failed) yields FactoryAnchored=null —
        // "cannot compare", not a divergence storm.
        var sets = Sets(all: ["0xaaa"], anchored: null);

        var (verdict, diverged) = CirclesPaymentsPoller.DecideFactoryAwareEligibility(
            sets, "0xaaa", factoryConfigured: true, enforced: false);

        Assert.That(verdict, Is.True, "legacy verdict decides in shadow");
        Assert.That(diverged, Is.False);
    }

    [Test]
    public void Shadow_LegacyVerdictWins_DivergenceReported()
    {
        var sets = Sets(all: ["0xaaa"], anchored: []);

        var (verdict, diverged) = CirclesPaymentsPoller.DecideFactoryAwareEligibility(
            sets, "0xaaa", factoryConfigured: true, enforced: false);

        Assert.That(verdict, Is.True, "shadow must not change the money flow");
        Assert.That(diverged, Is.True);
    }

    [Test]
    public void Shadow_AgreeingVerdicts_NoDivergence()
    {
        var sets = Sets(all: ["0xaaa"], anchored: ["0xaaa"]);

        var (verdict, diverged) = CirclesPaymentsPoller.DecideFactoryAwareEligibility(
            sets, "0xaaa", factoryConfigured: true, enforced: false);

        Assert.That(verdict, Is.True);
        Assert.That(diverged, Is.False);
    }

    [Test]
    public void FactoryUnset_LegacyOnly_NoDivergence()
    {
        var sets = Sets(all: ["0xaaa"], anchored: null);

        var (verdict, diverged) = CirclesPaymentsPoller.DecideFactoryAwareEligibility(
            sets, "0xbbb", factoryConfigured: false, enforced: false);

        Assert.That(verdict, Is.False);
        Assert.That(diverged, Is.False);
    }

    [Test]
    public void NullSets_Undetermined_InAllModes()
    {
        foreach (var (configured, enforced) in new[] { (false, false), (true, false), (true, true) })
        {
            var (verdict, diverged) = CirclesPaymentsPoller.DecideFactoryAwareEligibility(
                null, "0xaaa", configured, enforced);

            Assert.That(verdict, Is.Null, $"configured={configured} enforced={enforced}");
            Assert.That(diverged, Is.False);
        }
    }

    [Test]
    public void FactoryUnset_TrueVerdict_MatchesLegacy()
    {
        var sets = Sets(all: ["0xaaa"], anchored: null);

        var (verdict, diverged) = CirclesPaymentsPoller.DecideFactoryAwareEligibility(
            sets, "0xaaa", factoryConfigured: false, enforced: false);

        Assert.That(verdict, Is.True);
        Assert.That(diverged, Is.False);
    }
}

/// <summary>
/// Constructor validation of the factory-anchoring config: malformed addresses and
/// enforced-without-factory must refuse startup; a templated-empty value means "off".
/// Env-var driven, so non-parallelizable.
/// </summary>
[TestFixture]
[NonParallelizable]
public class GatewayFactoryConfigTests
{
    private static readonly string[] PollerEnv =
        ["RPC", "POSTGRES_CONNECTION", "PAYMENT_GATEWAY_FACTORY", "PAYMENT_GATEWAY_FACTORY_ENFORCED"];

    [SetUp]
    public void SetUp()
    {
        Environment.SetEnvironmentVariable("RPC", "http://rpc.test");
        Environment.SetEnvironmentVariable("POSTGRES_CONNECTION", "Host=test");
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var key in PollerEnv) Environment.SetEnvironmentVariable(key, null);
    }

    private static CirclesPaymentsPoller MakePoller()
    {
        var reconciler = new FulfillmentReconciler(
            new Moq.Mock<IStrandedFulfillmentSource>().Object,
            new Moq.Mock<IOrderLifecycleHooks>().Object,
            new Moq.Mock<Circles.Market.Api.Cart.IOrderStore>().Object,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FulfillmentReconciler>.Instance);
        return new CirclesPaymentsPoller(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CirclesPaymentsPoller>.Instance,
            new Moq.Mock<IHttpClientFactory>().Object,
            new Moq.Mock<IOrderPaymentFlow>().Object,
            new Moq.Mock<IPaymentStore>().Object,
            reconciler,
            new Moq.Mock<IOrderProcessingTraceSink>().Object);
    }

    [Test]
    public void InvalidFactoryAddress_RefusesStartup()
    {
        Environment.SetEnvironmentVariable("PAYMENT_GATEWAY_FACTORY", "not-an-address");

        Assert.Throws<InvalidOperationException>(() => MakePoller());
    }

    [Test]
    public void EnforcedWithoutFactory_RefusesStartup()
    {
        Environment.SetEnvironmentVariable("PAYMENT_GATEWAY_FACTORY_ENFORCED", "true");

        Assert.Throws<InvalidOperationException>(() => MakePoller());
    }

    [Test]
    public void EmptyStringFactory_IsTreatedAsUnset()
    {
        // The templated-unset shape (VAR=) must not crash startup.
        Environment.SetEnvironmentVariable("PAYMENT_GATEWAY_FACTORY", "");

        Assert.DoesNotThrow(() => MakePoller());
    }

    [Test]
    public void CsvFactories_AllValidated()
    {
        Environment.SetEnvironmentVariable("PAYMENT_GATEWAY_FACTORY",
            "0x186725d8fe10a573dc73144f7a317fcae5314f19, 0xBADBADBADBADBADBADBADBADBADBADBADBADBAD0");

        Assert.DoesNotThrow(() => MakePoller(),
            "csv of two well-formed addresses (case-insensitive) must parse");

        Environment.SetEnvironmentVariable("PAYMENT_GATEWAY_FACTORY",
            "0x186725d8fe10a573dc73144f7a317fcae5314f19,oops");
        Assert.Throws<InvalidOperationException>(() => MakePoller());
    }
}
