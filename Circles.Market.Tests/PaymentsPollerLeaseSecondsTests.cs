using Circles.Market.Api.Payments;

namespace Circles.Market.Tests;

[TestFixture]
public class PaymentsPollerLeaseSecondsTests
{
    [Test]
    public void Default_Is_Max_30_Or_6x_Poll_Interval()
    {
        // 6x small interval is below the 30s floor → floor wins.
        Assert.That(CirclesPaymentsPoller.ComputeLeaseSeconds(5, null), Is.EqualTo(30));
        // 6x larger interval exceeds the floor → multiplier wins.
        Assert.That(CirclesPaymentsPoller.ComputeLeaseSeconds(10, null), Is.EqualTo(60));
        Assert.That(CirclesPaymentsPoller.ComputeLeaseSeconds(20, ""), Is.EqualTo(120));
    }

    [Test]
    public void Explicit_Override_Is_Honoured_When_Above_Floor()
    {
        Assert.That(CirclesPaymentsPoller.ComputeLeaseSeconds(5, "45"), Is.EqualTo(45));
        Assert.That(CirclesPaymentsPoller.ComputeLeaseSeconds(10, "90"), Is.EqualTo(90));
    }

    [Test]
    public void Explicit_Override_Is_Floored_To_One_Past_Poll_Interval()
    {
        // An override that is too short to outlive a cycle is clamped to pollSeconds + 1.
        Assert.That(CirclesPaymentsPoller.ComputeLeaseSeconds(5, "3"), Is.EqualTo(6));
        Assert.That(CirclesPaymentsPoller.ComputeLeaseSeconds(5, "0"), Is.EqualTo(6));
        Assert.That(CirclesPaymentsPoller.ComputeLeaseSeconds(10, "10"), Is.EqualTo(11));
    }

    [Test]
    public void NonNumeric_Override_Falls_Back_To_Default()
    {
        Assert.That(CirclesPaymentsPoller.ComputeLeaseSeconds(5, "abc"), Is.EqualTo(30));
        Assert.That(CirclesPaymentsPoller.ComputeLeaseSeconds(10, "  "), Is.EqualTo(60));
    }

    [Test]
    public void Zero_Or_Negative_Poll_Interval_Is_Treated_As_One()
    {
        // Guards against a misconfigured non-positive poll interval producing a nonsensical lease.
        Assert.That(CirclesPaymentsPoller.ComputeLeaseSeconds(0, null), Is.EqualTo(30));
        Assert.That(CirclesPaymentsPoller.ComputeLeaseSeconds(-5, "2"), Is.EqualTo(2));
    }
}
