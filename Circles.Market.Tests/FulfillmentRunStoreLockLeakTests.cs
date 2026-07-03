using Circles.Market.Adapters.CodeDispenser;
using Circles.Market.Adapters.Odoo.Db;
using Circles.Market.Adapters.Unlock.Db;
using Circles.Market.Adapters.WooCommerce.Db;
using Circles.Market.Fulfillment.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Circles.Market.Tests;

/// <summary>
/// Regression tests for the fulfillment lock-leak. Terminal status writes (mark ok / error /
/// order-info) are driven from a catch block whose CancellationToken is the fulfillment timeout /
/// app-shutdown token — i.e. already cancelled when a timeout or restart is what aborted the job.
/// If a store honors that cancelled token the write is skipped and the run is stranded in its
/// in-progress state, which the idempotency guard then treats as "already running" — permanently
/// blocking re-drive. All four adapter stores must shield their terminal writes from that token.
///
/// Integration test: requires a real Postgres. Set MARKET_TEST_PG to a connection string
/// (e.g. "Host=localhost;Port=55432;Database=testdb;Username=postgres;Password=test").
/// Skipped automatically when the variable is absent.
/// </summary>
[TestFixture]
public class FulfillmentRunStoreLockLeakTests
{
    private string _conn = null!;
    private const long ChainId = 100;
    private const string Seller = "0xabc0000000000000000000000000000000000001";

    [SetUp]
    public void SetUp()
    {
        var conn = Environment.GetEnvironmentVariable("MARKET_TEST_PG");
        if (string.IsNullOrWhiteSpace(conn))
        {
            // In CI these tests are part of the image-publish gate — a silent skip would make it vacuous.
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
            {
                Assert.Fail("MARKET_TEST_PG is not set in CI; fulfillment-run integration tests must run, not skip.");
            }
            Assert.Ignore("Set MARKET_TEST_PG to a Postgres connection string to run fulfillment-run store integration tests.");
        }
        _conn = conn!;
    }

    private static async Task ExecAsync(string conn, string sql, Action<NpgsqlParameterCollection>? bind = null)
    {
        await using var c = new NpgsqlConnection(conn);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        bind?.Invoke(cmd.Parameters);
        await cmd.ExecuteNonQueryAsync();
    }

    // Odoo / Unlock / CodeDispenser share the same run-table shape and IFulfillmentRunStore
    // contract (started -> ok | error). The store SQL hardcodes the table name, so each case
    // uses its real table name.
    private static IEnumerable<TestCaseData> StandardStores()
    {
        yield return new TestCaseData(
            "fulfillment_runs",
            (Func<string, IFulfillmentRunStore>)(c => new PostgresFulfillmentRunStore(c, NullLogger<PostgresFulfillmentRunStore>.Instance)))
            .SetName("Odoo");
        yield return new TestCaseData(
            "unlock_fulfillment_runs",
            (Func<string, IFulfillmentRunStore>)(c => new PostgresUnlockFulfillmentRunStore(c)))
            .SetName("Unlock");
        yield return new TestCaseData(
            "code_fulfillment_runs",
            (Func<string, IFulfillmentRunStore>)(c => new PostgresCodeFulfillmentRunStore(c)))
            .SetName("CodeDispenser");
    }

    [TestCaseSource(nameof(StandardStores))]
    public async Task Terminal_writes_survive_cancelled_caller_token(string table, Func<string, IFulfillmentRunStore> factory)
    {
        await ExecAsync(_conn, $@"
CREATE TABLE IF NOT EXISTS {table} (
  chain_id bigint NOT NULL, seller_address text NOT NULL, payment_reference text NOT NULL,
  order_id text NOT NULL, status text NOT NULL, last_error text NULL,
  created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),
  completed_at timestamptz NULL,
  PRIMARY KEY (chain_id, seller_address, payment_reference));");

        string payRef = "pay_LEAK_" + table;
        string orderId = "ord_LEAK_" + table;
        await ExecAsync(_conn, $"DELETE FROM {table} WHERE payment_reference=@p;", p => p.AddWithValue("@p", payRef));

        var store = factory(_conn);

        // 1) MarkError under an already-cancelled caller token must still leave 'started'.
        var (acquired, status) = await store.TryBeginAsync(ChainId, Seller, payRef, orderId, CancellationToken.None);
        Assert.That(acquired, Is.True);
        Assert.That(status, Is.EqualTo("started"));

        await MarkWithCancelledTokenAsync(ct => store.MarkErrorAsync(ChainId, Seller, payRef, "simulated timeout abort", ct));
        Assert.That(await store.GetStatusAsync(ChainId, Seller, payRef, CancellationToken.None), Is.EqualTo("error"),
            "run must transition off 'started' even when the caller token was cancelled");

        // 2) MarkOk under a cancelled token (re-acquired from the 'error' state).
        var (reacquired, _) = await store.TryBeginAsync(ChainId, Seller, payRef, orderId, CancellationToken.None);
        Assert.That(reacquired, Is.True, "an errored run must be re-acquirable");

        await MarkWithCancelledTokenAsync(ct => store.MarkOkAsync(ChainId, Seller, payRef, ct));
        Assert.That(await store.GetStatusAsync(ChainId, Seller, payRef, CancellationToken.None), Is.EqualTo("ok"));
    }

    [Test]
    public async Task WooCommerce_terminal_writes_survive_cancelled_caller_token()
    {
        await ExecAsync(_conn, @"
CREATE TABLE IF NOT EXISTS wc_fulfillment_runs (
  id uuid NOT NULL DEFAULT gen_random_uuid(), chain_id bigint NOT NULL, seller_address text NOT NULL,
  payment_reference text NOT NULL, idempotency_key uuid NOT NULL UNIQUE, wc_order_id integer NULL,
  wc_order_number text NULL, status text NOT NULL, outcome text NULL, error_detail text NULL,
  request_payload jsonb NOT NULL DEFAULT '{}', response_payload jsonb NULL,
  created_at timestamptz NOT NULL DEFAULT now(), completed_at timestamptz NULL, PRIMARY KEY (id));
CREATE UNIQUE INDEX IF NOT EXISTS ux_wc_fulfillment_runs_natural
  ON wc_fulfillment_runs(chain_id, seller_address, payment_reference);");

        const string payRef = "pay_LEAK_wc";
        await ExecAsync(_conn, "DELETE FROM wc_fulfillment_runs WHERE payment_reference=@p;", p => p.AddWithValue("@p", payRef));

        var store = new PostgresWooCommerceFulfillmentRunStore(_conn, NullLogger<PostgresWooCommerceFulfillmentRunStore>.Instance);
        var (acquired, status) = await store.TryBeginAsync(ChainId, Seller, payRef, "ord_LEAK_wc", CancellationToken.None);
        Assert.That(acquired, Is.True);
        Assert.That(status, Is.EqualTo("pending"));

        await MarkWithCancelledTokenAsync(ct => store.MarkErrorAsync(ChainId, Seller, payRef, "simulated timeout abort", ct));
        Assert.That(await store.GetStatusAsync(ChainId, Seller, payRef, CancellationToken.None), Is.EqualTo("failed"),
            "run must transition off 'pending' even when the caller token was cancelled");
    }

    private static async Task MarkWithCancelledTokenAsync(Func<CancellationToken, Task> mark)
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await mark(cancelled.Token);
    }
}
