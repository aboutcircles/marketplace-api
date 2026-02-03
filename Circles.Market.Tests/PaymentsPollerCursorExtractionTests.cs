using Circles.Market.Api.Payments;

namespace Circles.Market.Tests;

[TestFixture]
public class PaymentsPollerCursorExtractionTests
{
    [Test]
    public void TryExtractCursor_ReturnsTrue_For_Valid_Row()
    {
        var cols = new[] { "blockNumber", "transactionIndex", "logIndex" };
        object[] row = { 123L, 4, 7 };

        var ok = CirclesPaymentsPoller.TryExtractCursor(cols, row, out var block, out var tx, out var log);
        Assert.That(ok, Is.True);
        Assert.That(block, Is.EqualTo(123L));
        Assert.That(tx, Is.EqualTo(4));
        Assert.That(log, Is.EqualTo(7));
    }

    [Test]
    public void TryExtractCursor_ReturnsFalse_When_Columns_Missing()
    {
        var cols = new[] { "blockNumber", "transactionIndex" };
        object[] row = { 123L, 4 };

        var ok = CirclesPaymentsPoller.TryExtractCursor(cols, row, out _, out _, out _);
        Assert.That(ok, Is.False);
    }

    [Test]
    public void TryExtractCursor_ReturnsFalse_When_Row_TooShort()
    {
        var cols = new[] { "blockNumber", "transactionIndex", "logIndex" };
        object[] row = { 123L, 4 };

        var ok = CirclesPaymentsPoller.TryExtractCursor(cols, row, out _, out _, out _);
        Assert.That(ok, Is.False);
    }
}
