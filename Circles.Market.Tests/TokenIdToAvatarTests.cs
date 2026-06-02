using System.Numerics;
using Circles.Market.Api.Payments;

namespace Circles.Market.Tests;

[TestFixture]
public class TokenIdToAvatarTests
{
    private static BigInteger TokenIdFromAddress(string hex40)
    {
        // CRC v2: toTokenId(avatar) = uint256(uint160(avatar)).
        var bytes = Convert.FromHexString(hex40);
        return new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
    }

    [Test]
    public void TokenId_Equals_Uint160_Of_Avatar()
    {
        const string addr = "4bfc74983d6338d3395a00118546614bb78472c2";
        var tokenId = TokenIdFromAddress(addr);

        var avatar = CirclesPaymentsPoller.ToAvatarAddress(tokenId);

        Assert.That(avatar, Is.EqualTo("0x" + addr));
    }

    [Test]
    public void High_Bits_Above_160_Are_Masked_Off()
    {
        const string addr = "943186fbcfd74fd575bcf9aa76a53f56b2f06aba";
        // Set bits well above bit 160 to simulate a type-tagged id; avatar must be unchanged.
        var tokenId = TokenIdFromAddress(addr) + (BigInteger.One << 200);

        var avatar = CirclesPaymentsPoller.ToAvatarAddress(tokenId);

        Assert.That(avatar, Is.EqualTo("0x" + addr));
    }

    [Test]
    public void Zero_TokenId_Maps_To_Zero_Address()
    {
        var avatar = CirclesPaymentsPoller.ToAvatarAddress(BigInteger.Zero);

        Assert.That(avatar, Is.EqualTo("0x0000000000000000000000000000000000000000"));
    }

    [Test]
    public void Small_TokenId_Is_Left_Padded_To_20_Bytes()
    {
        var avatar = CirclesPaymentsPoller.ToAvatarAddress(new BigInteger(1));

        Assert.That(avatar, Is.EqualTo("0x0000000000000000000000000000000000000001"));
    }

    [Test]
    public void Output_Is_Always_Lowercase()
    {
        const string addr = "AABBCCDDEEFF00112233445566778899AABBCCDD";
        var tokenId = TokenIdFromAddress(addr);

        var avatar = CirclesPaymentsPoller.ToAvatarAddress(tokenId);

        Assert.That(avatar, Is.EqualTo("0x" + addr.ToLowerInvariant()));
    }
}
