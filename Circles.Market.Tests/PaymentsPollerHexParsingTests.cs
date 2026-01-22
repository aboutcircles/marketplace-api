using Circles.Market.Api.Payments;

namespace Circles.Market.Tests;

[TestFixture]
public class PaymentsPollerHexParsingTests
{
    [TestCase("0x0", 0L)]
    [TestCase("0x00", 0L)]
    [TestCase("0xa", 10L)]
    [TestCase("0x10", 16L)]
    [TestCase("0XFF", 255L)]
    [TestCase("ff", 255L)]
    public void ParseEthHexBlock_Works_For_Common_Forms(string hex, long expected)
    {
        var v = CirclesPaymentsPoller.ParseEthHexBlock(hex);
        Assert.That(v, Is.EqualTo(expected));
    }

    [Test]
    public void ParseEthHexBlock_Empty_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => CirclesPaymentsPoller.ParseEthHexBlock(""));
    }

    [Test]
    public void ParseEthHexBlock_TooLarge_Throws()
    {
        // value > long.MaxValue (e.g., 0x1_0000_0000_0000_0000)
        Assert.Throws<OverflowException>(() => CirclesPaymentsPoller.ParseEthHexBlock("0x10000000000000000"));
    }

    [TestCase("pay_1D5B0445F1EAC4F0CAACE0E673691FEC", "pay_1D5B0445F1EAC4F0CAACE0E673691FEC")]
    [TestCase("pay_1d5b0445f1eac4f0caace0e673691fec", "pay_1D5B0445F1EAC4F0CAACE0E673691FEC")]
    [TestCase("PAY_1d5b0445f1eac4f0caace0e673691fec", "pay_1D5B0445F1EAC4F0CAACE0E673691FEC")]
    [TestCase("  pay_1D5B0445F1EAC4F0CAACE0E673691FEC  ", "pay_1D5B0445F1EAC4F0CAACE0E673691FEC")]
    [TestCase("pay_1D5B0445F1EAC4F0CAACE0E673691FEC\0", null)]
    [TestCase("pay_123", null)]
    [TestCase("pay_1D5B0445F1EAC4F0CAACE0E673691FEZ", null)]
    [TestCase("", null)]
    [TestCase(null, null)]
    public void NormalizePaymentReference_Works(string? input, string? expected)
    {
        var v = CirclesPaymentsPoller.NormalizePaymentReference(input);
        Assert.That(v, Is.EqualTo(expected));
    }
}
