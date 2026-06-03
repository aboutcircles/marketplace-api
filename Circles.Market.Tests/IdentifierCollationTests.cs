using Circles.Market.Shared.Db;
using Microsoft.Extensions.Logging.Abstractions;

namespace Circles.Market.Tests;

[TestFixture]
public class IdentifierCollationTests
{
    // The migration is best-effort and must never prevent an adapter from starting. An unreachable
    // database must be swallowed (logged, not thrown). Short connect/command timeouts keep this fast.
    [Test]
    public void EnsureCollation_Is_NonFatal_When_Database_Unreachable()
    {
        const string unreachable =
            "Host=127.0.0.1;Port=1;Database=nope;Username=nope;Password=nope;Timeout=1;Command Timeout=1";

        Assert.DoesNotThrowAsync(async () =>
            await IdentifierCollation.EnsureIndexKeyTextColumnsCollatedToCAsync(
                unreachable, NullLogger.Instance, "test"));
    }
}
