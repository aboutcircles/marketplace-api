using System;
using System.Numerics;
using Circles.Market.Api.Payments;

namespace Circles.Market.Tests;

[TestFixture]
public class GatewayTrustParsingTests
{
    private static readonly string[] Cols = { "trustReceiver", "expiry" };

    private const long Now = 1_000_000;
    private const long Future = 2_000_000;
    private const long Past = 500_000;

    [Test]
    public void Active_Receiver_Is_Included()
    {
        object[][] rows =
        {
            new object[] { "0xAAA", Future }
        };

        var set = CirclesPaymentsPoller.ParseActiveTrustSet(Cols, rows, Now);

        Assert.That(set, Is.EquivalentTo(new[] { "0xaaa" }));
    }

    [Test]
    public void Expired_Receiver_Is_Excluded()
    {
        object[][] rows =
        {
            new object[] { "0xAAA", Past }
        };

        var set = CirclesPaymentsPoller.ParseActiveTrustSet(Cols, rows, Now);

        Assert.That(set, Is.Empty);
    }

    // Columns including position fields so "latest wins" is decided by (block, tx, log), not order.
    private static readonly string[] ColsPos = { "trustReceiver", "expiry", "blockNumber", "transactionIndex", "logIndex" };

    [Test]
    public void Latest_Row_Per_Receiver_Wins_Revocation()
    {
        // A later revocation (higher block, past expiry) overrides an earlier active trust —
        // even when the rows are supplied out of block order.
        object[][] rows =
        {
            new object[] { "0xAAA", Past, 20L, 0, 0 },   // later: revoked
            new object[] { "0xAAA", Future, 10L, 0, 0 }  // earlier: active
        };

        var set = CirclesPaymentsPoller.ParseActiveTrustSet(ColsPos, rows, Now);

        Assert.That(set, Is.Empty);
    }

    [Test]
    public void Latest_Row_Per_Receiver_Wins_Reactivation()
    {
        object[][] rows =
        {
            new object[] { "0xAAA", Future, 20L, 0, 0 }, // later: re-activated
            new object[] { "0xAAA", Past, 10L, 0, 0 }    // earlier: revoked
        };

        var set = CirclesPaymentsPoller.ParseActiveTrustSet(ColsPos, rows, Now);

        Assert.That(set, Is.EquivalentTo(new[] { "0xaaa" }));
    }

    [Test]
    public void Latest_Row_Decided_By_Tx_And_Log_Within_Same_Block()
    {
        object[][] rows =
        {
            new object[] { "0xAAA", Future, 10L, 0, 0 }, // same block, earlier log
            new object[] { "0xAAA", Past, 10L, 0, 1 }    // same block, later log → wins (revoked)
        };

        var set = CirclesPaymentsPoller.ParseActiveTrustSet(ColsPos, rows, Now);

        Assert.That(set, Is.Empty);
    }

    [Test]
    public void Expiry_Parses_From_String_Values()
    {
        object[][] rows =
        {
            new object[] { "0xAAA", Future.ToString() },
            new object[] { "0xBBB", Past.ToString() }
        };

        var set = CirclesPaymentsPoller.ParseActiveTrustSet(Cols, rows, Now);

        Assert.That(set, Is.EquivalentTo(new[] { "0xaaa" }));
    }

    [Test]
    public void Missing_Columns_Yield_Empty_Set()
    {
        var cols = new[] { "trustReceiver" }; // no expiry column
        object[][] rows =
        {
            new object[] { "0xAAA" }
        };

        var set = CirclesPaymentsPoller.ParseActiveTrustSet(cols, rows, Now);

        Assert.That(set, Is.Empty);
    }

    [Test]
    public void Multiple_Receivers_Filtered_By_Expiry()
    {
        object[][] rows =
        {
            new object[] { "0xAAA", Future },
            new object[] { "0xBBB", Past },
            new object[] { "0xCCC", Future }
        };

        var set = CirclesPaymentsPoller.ParseActiveTrustSet(Cols, rows, Now);

        Assert.That(set, Is.EquivalentTo(new[] { "0xaaa", "0xccc" }));
    }

    [Test]
    public void Full_Length_Checksummed_TrustReceiver_Matches_ToAvatarAddress_Output()
    {
        // The indexer may return trustReceiver checksummed; it must still match the lowercase
        // 0x+40hex that ToAvatarAddress produces from the same token id.
        const string addr = "4bfc74983d6338d3395a00118546614bb78472c2";
        object[][] rows = { new object[] { "0x4BFC74983d6338D3395a00118546614bb78472C2", Future, 10L, 0, 0 } };

        var set = CirclesPaymentsPoller.ParseActiveTrustSet(ColsPos, rows, Now);

        var tokenAvatar = CirclesPaymentsPoller.ToAvatarAddress(
            new BigInteger(Convert.FromHexString(addr), isUnsigned: true, isBigEndian: true));
        Assert.That(tokenAvatar, Is.EqualTo("0x" + addr));
        Assert.That(set, Does.Contain(tokenAvatar));
    }

    [Test]
    public void TrustReceiver_Without_0x_Prefix_Is_Normalized()
    {
        object[][] rows = { new object[] { "AAB0000000000000000000000000000000000001", Future, 10L, 0, 0 } };

        var set = CirclesPaymentsPoller.ParseActiveTrustSet(ColsPos, rows, Now);

        Assert.That(set, Does.Contain("0xaab0000000000000000000000000000000000001"));
    }

    [Test]
    public void Expiry_Exactly_Now_Is_Excluded()
    {
        // Active requires expiry strictly greater than now.
        object[][] rows = { new object[] { "0xAAA", Now } };

        var set = CirclesPaymentsPoller.ParseActiveTrustSet(Cols, rows, Now);

        Assert.That(set, Is.Empty);
    }

    [Test]
    public void Expiry_One_Second_In_Future_Is_Included()
    {
        object[][] rows = { new object[] { "0xAAA", Now + 1 } };

        var set = CirclesPaymentsPoller.ParseActiveTrustSet(Cols, rows, Now);

        Assert.That(set, Is.EquivalentTo(new[] { "0xaaa" }));
    }
}
