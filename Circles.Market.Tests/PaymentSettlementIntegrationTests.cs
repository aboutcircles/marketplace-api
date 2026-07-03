using System.Numerics;
using Circles.Market.Api;
using Circles.Market.Api.Cart;
using Circles.Market.Api.Payments;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Circles.Market.Tests;

/// <summary>
/// Integration tests for the settlement core: the money-deciding logic lives in SQL
/// (<see cref="PostgresOrderStore.TryMarkPaidByReference"/> amount gate and
/// <see cref="PostgresPaymentStore.UpsertAndGetPayment"/> eligible-only aggregation),
/// so it must be exercised against a real Postgres, not mocks.
///
/// Requires a real Postgres. Set MARKET_TEST_PG to a connection string
/// (e.g. "Host=localhost;Port=5432;Database=market_test;Username=postgres;Password=test").
/// Skipped automatically when the variable is absent.
/// </summary>
[TestFixture]
public class PaymentSettlementIntegrationTests
{
    private const long ChainId = 100;
    private const string Gateway = "0x00000000000000000000000000000000000000gw";
    private const string Seller = "0xabc0000000000000000000000000000000000002";
    private const string Buyer = "0xdef0000000000000000000000000000000000003";

    private static readonly BigInteger OneCrc = BigInteger.Pow(10, 18);

    private string _conn = null!;
    private PostgresPaymentStore _payments = null!;
    private PostgresOrderStore _orders = null!;

    [SetUp]
    public void SetUp()
    {
        var conn = Environment.GetEnvironmentVariable("MARKET_TEST_PG");
        if (string.IsNullOrWhiteSpace(conn))
        {
            // In CI these tests ARE the image-publish gate — a silent skip would make it vacuous.
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
            {
                Assert.Fail("MARKET_TEST_PG is not set in CI; settlement integration tests must run, not skip.");
            }
            Assert.Ignore("Set MARKET_TEST_PG to a Postgres connection string to run settlement integration tests.");
        }

        _conn = conn!;
        _payments = new PostgresPaymentStore(_conn, NullLogger<PostgresPaymentStore>.Instance);
        _orders = new PostgresOrderStore(_conn, NullLogger<PostgresOrderStore>.Instance);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static string NewRef() => "pay_TEST_" + Guid.NewGuid().ToString("N");

    private static OrderSnapshot MakeOrder(string payRef, decimal? price)
    {
        return new OrderSnapshot
        {
            OrderNumber = "ord_" + Guid.NewGuid().ToString("N"),
            PaymentReference = payRef,
            OrderDate = DateTimeOffset.UtcNow.ToString("O"),
            Customer = new SchemaOrgPersonId { Id = $"eip155:{ChainId}:{Buyer}" },
            AcceptedOffer =
            {
                new OfferSnapshot
                {
                    Price = price,
                    PriceCurrency = "CRC",
                    Seller = new SchemaOrgOrgId { Id = $"eip155:{ChainId}:{Seller}" }
                }
            },
            OrderedItem = { new OrderItemLine { OrderQuantity = 1 } },
            TotalPaymentDue = price is null
                ? null
                : new PriceSpecification { Price = price, PriceCurrency = "CRC" }
        };
    }

    private string CreateOrder(decimal? price, string payRef)
    {
        var order = MakeOrder(payRef, price);
        Assert.That(_orders.Create(order.OrderNumber, "bsk_" + Guid.NewGuid().ToString("N"), order), Is.True);
        return order.OrderNumber;
    }

    private PaymentTransferRecord Transfer(
        string payRef, BigInteger amountWei, bool? eligible, int logIndex = 0)
    {
        return new PaymentTransferRecord(
            ChainId: ChainId,
            TxHash: "0x" + Guid.NewGuid().ToString("N"),
            LogIndex: logIndex,
            TransactionIndex: 0,
            BlockNumber: 1,
            PaymentReference: payRef,
            GatewayAddress: Gateway,
            PayerAddress: Buyer,
            AmountWei: amountWei,
            CreatedAt: DateTimeOffset.UtcNow,
            TokenId: BigInteger.One,
            TokenAvatar: "0x00000000000000000000000000000000000000aa",
            Eligible: eligible);
    }

    /// <summary>Observe a transfer, re-aggregate, and attempt settlement — the poller's DB sequence.</summary>
    private bool ObserveAndTrySettle(PaymentTransferRecord t)
    {
        _payments.UpsertObservedTransfer(t);
        var agg = _payments.UpsertAndGetPayment(ChainId, t.PaymentReference);
        Assert.That(agg, Is.Not.Null, "aggregation must produce a payments row once a transfer exists");
        return _orders.TryMarkPaidByReference(
            paymentReference: agg!.PaymentReference,
            paidChainId: agg.ChainId,
            txHash: agg.FirstTxHash ?? string.Empty,
            logIndex: agg.FirstLogIndex ?? 0,
            gatewayAddress: agg.GatewayAddress,
            amountWei: agg.TotalAmountWei,
            paidAt: agg.CreatedAt);
    }

    private (string Status, DateTimeOffset? PaidAt, string? PaidAmountWei) ReadOrderPaidState(string orderId)
    {
        using var conn = new NpgsqlConnection(_conn);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT status, paid_at, paid_amount_wei::text FROM orders WHERE order_id=@id";
        cmd.Parameters.AddWithValue("@id", orderId);
        using var r = cmd.ExecuteReader();
        Assert.That(r.Read(), Is.True, $"order {orderId} not found");
        return (
            r.GetString(0),
            r.IsDBNull(1) ? null : r.GetFieldValue<DateTimeOffset>(1),
            r.IsDBNull(2) ? null : r.GetString(2));
    }

    // ── amount gate ─────────────────────────────────────────────────────────

    [Test]
    public void ExactAmount_Settles()
    {
        string payRef = NewRef();
        string orderId = CreateOrder(price: 5m, payRef);

        bool settled = ObserveAndTrySettle(Transfer(payRef, 5 * OneCrc, eligible: true));

        Assert.That(settled, Is.True);
        var state = ReadOrderPaidState(orderId);
        Assert.That(state.Status, Is.EqualTo(StatusUris.PaymentProcessing));
        Assert.That(state.PaidAt, Is.Not.Null);
        Assert.That(state.PaidAmountWei, Is.EqualTo((5 * OneCrc).ToString()));
    }

    [Test]
    public void Underpayment_DoesNotSettle_ThenTopUpSettles()
    {
        string payRef = NewRef();
        string orderId = CreateOrder(price: 5m, payRef);

        // 4 CRC of 5 — must NOT settle
        bool settled = ObserveAndTrySettle(Transfer(payRef, 4 * OneCrc, eligible: true, logIndex: 0));
        Assert.That(settled, Is.False, "underpayment must not mark the order paid");
        Assert.That(ReadOrderPaidState(orderId).PaidAt, Is.Null);

        // Second transfer tops the aggregate up to 5 CRC — multi-transfer aggregation settles
        settled = ObserveAndTrySettle(Transfer(payRef, 1 * OneCrc, eligible: true, logIndex: 1));
        Assert.That(settled, Is.True, "aggregated eligible total meeting the price must settle");
        Assert.That(ReadOrderPaidState(orderId).PaidAmountWei, Is.EqualTo((5 * OneCrc).ToString()));
    }

    [Test]
    public void Overpayment_Settles()
    {
        string payRef = NewRef();
        CreateOrder(price: 5m, payRef);

        Assert.That(ObserveAndTrySettle(Transfer(payRef, 6 * OneCrc, eligible: true)), Is.True);
    }

    [Test]
    public void Settlement_IsIdempotent()
    {
        string payRef = NewRef();
        string orderId = CreateOrder(price: 1m, payRef);

        Assert.That(ObserveAndTrySettle(Transfer(payRef, OneCrc, eligible: true, logIndex: 0)), Is.True);
        var first = ReadOrderPaidState(orderId);

        // Re-driving the same reference (new observed transfer) must not change paid state again
        bool changedAgain = ObserveAndTrySettle(Transfer(payRef, OneCrc, eligible: true, logIndex: 1));
        Assert.That(changedAgain, Is.False, "an already-paid order must not be re-marked");
        Assert.That(ReadOrderPaidState(orderId).PaidAt, Is.EqualTo(first.PaidAt));
    }

    // ── token-trust eligibility (tri-state) ─────────────────────────────────

    [Test]
    public void IneligibleTransfers_NeverCredit()
    {
        string payRef = NewRef();
        string orderId = CreateOrder(price: 1m, payRef);

        // 10 CRC of untrusted-token value — recorded, never credited
        bool settled = ObserveAndTrySettle(Transfer(payRef, 10 * OneCrc, eligible: false));

        Assert.That(settled, Is.False, "untrusted-token value must never settle an order");
        Assert.That(ReadOrderPaidState(orderId).PaidAt, Is.Null);

        var agg = _payments.UpsertAndGetPayment(ChainId, payRef);
        Assert.That(agg!.TotalAmountWei, Is.Null, "eligible-only sum must exclude ineligible transfers");
    }

    [Test]
    public void UndeterminedTransfers_DoNotCredit_UntilFlippedEligible()
    {
        string payRef = NewRef();
        string orderId = CreateOrder(price: 2m, payRef);

        var t = Transfer(payRef, 2 * OneCrc, eligible: null);
        Assert.That(ObserveAndTrySettle(t), Is.False, "undetermined trust must not credit");
        Assert.That(ReadOrderPaidState(orderId).PaidAt, Is.Null);

        // Poller re-evaluation pass resolves trust → transfer becomes eligible → settle
        _payments.SetTransferEligibility(ChainId, t.TxHash, t.LogIndex, eligible: true);
        var agg = _payments.UpsertAndGetPayment(ChainId, payRef);
        Assert.That(agg!.TotalAmountWei, Is.EqualTo(2 * OneCrc));

        bool settled = _orders.TryMarkPaidByReference(
            payRef, ChainId, agg.FirstTxHash ?? "", agg.FirstLogIndex ?? 0,
            agg.GatewayAddress, agg.TotalAmountWei, agg.CreatedAt);
        Assert.That(settled, Is.True, "flip to eligible must allow settlement");
    }

    [Test]
    public void MixedEligibility_OnlyEligibleValueCounts()
    {
        string payRef = NewRef();
        string orderId = CreateOrder(price: 5m, payRef);

        _payments.UpsertObservedTransfer(Transfer(payRef, 3 * OneCrc, eligible: true, logIndex: 0));
        _payments.UpsertObservedTransfer(Transfer(payRef, 10 * OneCrc, eligible: false, logIndex: 1));
        _payments.UpsertObservedTransfer(Transfer(payRef, 10 * OneCrc, eligible: null, logIndex: 2));

        var agg = _payments.UpsertAndGetPayment(ChainId, payRef);
        Assert.That(agg!.TotalAmountWei, Is.EqualTo(3 * OneCrc), "only eligible transfers may contribute");

        bool settled = _orders.TryMarkPaidByReference(
            payRef, ChainId, agg.FirstTxHash ?? "", agg.FirstLogIndex ?? 0,
            agg.GatewayAddress, agg.TotalAmountWei, agg.CreatedAt);
        Assert.That(settled, Is.False, "3 CRC eligible of 5 CRC due must not settle");
        Assert.That(ReadOrderPaidState(orderId).PaidAt, Is.Null);
    }

    [Test]
    public void SetTransferEligibility_IsOneShot()
    {
        string payRef = NewRef();
        var t = Transfer(payRef, OneCrc, eligible: null);
        _payments.UpsertObservedTransfer(t);

        _payments.SetTransferEligibility(ChainId, t.TxHash, t.LogIndex, eligible: false);
        // Second write must be a no-op: eligibility is only resolvable from the undetermined state
        _payments.SetTransferEligibility(ChainId, t.TxHash, t.LogIndex, eligible: true);

        var stored = _payments.GetTransfersByReference(ChainId, payRef).Single();
        Assert.That(stored.Eligible, Is.False, "a determined eligibility verdict must be immutable");
    }

    // ── edge: zero-price orders (documents current behavior) ────────────────

    [Test]
    public void ZeroPriceOrder_SettlesOnAnyEligibleTransfer()
    {
        // Pins current behavior: expected_wei = 0, so any eligible transfer amount (>= 0) settles.
        // If the zero-price policy changes, this test must change with it.
        string payRef = NewRef();
        string orderId = CreateOrder(price: 0m, payRef);

        bool settled = ObserveAndTrySettle(Transfer(payRef, BigInteger.One, eligible: true));

        Assert.That(settled, Is.True);
        Assert.That(ReadOrderPaidState(orderId).Status, Is.EqualTo(StatusUris.PaymentProcessing));
    }

    [Test]
    public void NullPriceOrder_RequiresEligibleValue()
    {
        // An order without totalPaymentDue.price (not creatable via checkout, but accepted by
        // IOrderStore.Create) has no amount gate — it must still never settle on a payment
        // whose transfers are all untrusted/undetermined (zero eligible value).
        string payRef = NewRef();
        string orderId = CreateOrder(price: null, payRef);

        bool settled = ObserveAndTrySettle(Transfer(payRef, 10 * OneCrc, eligible: false, logIndex: 0));
        Assert.That(settled, Is.False, "zero eligible value must never settle, even without a price gate");
        Assert.That(ReadOrderPaidState(orderId).PaidAt, Is.Null);

        // Any eligible value settles a price-less order (documents current policy)
        settled = ObserveAndTrySettle(Transfer(payRef, BigInteger.One, eligible: true, logIndex: 1));
        Assert.That(settled, Is.True);
    }

    // ── confirm / finalize transitions ──────────────────────────────────────

    [Test]
    public void ConfirmAndFinalize_AdvanceOnlyPaidOrders()
    {
        string payRef = NewRef();
        CreateOrder(price: 1m, payRef);
        var now = DateTimeOffset.UtcNow;

        // Not paid yet — confirm/finalize must not advance anything
        Assert.That(_orders.TryMarkConfirmedByReference(payRef, now), Is.False);
        Assert.That(_orders.TryMarkFinalizedByReference(payRef, now), Is.False);

        Assert.That(ObserveAndTrySettle(Transfer(payRef, OneCrc, eligible: true)), Is.True);

        Assert.That(_orders.TryMarkConfirmedByReference(payRef, now), Is.True);
        Assert.That(_orders.TryMarkConfirmedByReference(payRef, now), Is.False, "confirm is idempotent");
        Assert.That(_orders.TryMarkFinalizedByReference(payRef, now), Is.True);
        Assert.That(_orders.TryMarkFinalizedByReference(payRef, now), Is.False, "finalize is idempotent");
    }
}
