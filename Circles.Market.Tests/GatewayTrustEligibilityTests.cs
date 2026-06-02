using Circles.Market.Api.Payments;

namespace Circles.Market.Tests;

[TestFixture]
public class GatewayTrustEligibilityTests
{
    [Test]
    public void Null_TrustSet_Is_Undetermined()
    {
        var result = CirclesPaymentsPoller.DecideEligibility(null, "0xaaa");

        Assert.That(result, Is.Null);
    }

    [Test]
    public void Missing_TokenAvatar_Is_Undetermined()
    {
        var set = new HashSet<string> { "0xaaa" };

        Assert.That(CirclesPaymentsPoller.DecideEligibility(set, null), Is.Null);
        Assert.That(CirclesPaymentsPoller.DecideEligibility(set, ""), Is.Null);
    }

    [Test]
    public void Trusted_Token_Is_Eligible()
    {
        var set = new HashSet<string> { "0xaaa", "0xbbb" };

        var result = CirclesPaymentsPoller.DecideEligibility(set, "0xaaa");

        Assert.That(result, Is.True);
    }

    [Test]
    public void Untrusted_Token_Is_Ineligible()
    {
        var set = new HashSet<string> { "0xaaa" };

        var result = CirclesPaymentsPoller.DecideEligibility(set, "0xccc");

        Assert.That(result, Is.False);
    }

    [Test]
    public void Empty_TrustSet_Means_Ineligible_Not_Undetermined()
    {
        // An empty-but-known trust set is a definite "trusts nothing", so a real token is ineligible.
        var set = new HashSet<string>();

        var result = CirclesPaymentsPoller.DecideEligibility(set, "0xaaa");

        Assert.That(result, Is.False);
    }

    [Test]
    public void Token_Avatar_Match_Is_Case_Insensitive()
    {
        var set = new HashSet<string> { "0xaaa" };

        var result = CirclesPaymentsPoller.DecideEligibility(set, "0xAAA");

        Assert.That(result, Is.True);
    }

    [Test]
    public void Token_Avatar_Without_0x_Prefix_Is_Normalized_Before_Match()
    {
        var set = new HashSet<string> { "0xaaa" };

        var result = CirclesPaymentsPoller.DecideEligibility(set, "AAA");

        Assert.That(result, Is.True);
    }
}
