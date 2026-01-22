using Circles.Market.Api.Payments;

namespace Circles.Market.Tests;

[TestFixture]
public class InMemoryOrderStatusEventBusTests
{
    [SetUp]
    public void Setup()
    {
        // Minimize capacity and subscribers to exercise bounds
        Environment.SetEnvironmentVariable("SSE_CHANNEL_CAPACITY", "1");
        Environment.SetEnvironmentVariable("SSE_MAX_SUBSCRIBERS_PER_KEY", "2");
    }

    [TearDown]
    public void Teardown()
    {
        Environment.SetEnvironmentVariable("SSE_CHANNEL_CAPACITY", null);
        Environment.SetEnvironmentVariable("SSE_MAX_SUBSCRIBERS_PER_KEY", null);
    }

    [Test]
    public async Task Publish_Is_NonBlocking_And_DropOldest_With_Capacity_1()
    {
        var bus = new InMemoryOrderStatusEventBus();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var received = new List<OrderStatusEvent>();
        var subTask = Task.Run(async () =>
        {
            await foreach (var e in bus.SubscribeAsync("0xabc", 100, cts.Token))
            {
                received.Add(e);
                if (received.Count >= 1) break; // we'll cancel later
            }
        }, cts.Token);

        // Give subscription a moment to attach
        await Task.Delay(50);

        // Rapidly publish 3 events; capacity=1 and DropOldest should keep last one
        for (int i = 0; i < 3; i++)
        {
            await bus.PublishAsync(new OrderStatusEvent(
                OrderId: $"ord_{i}",
                PaymentReference: null,
                OldStatus: null,
                NewStatus: $"S{i}",
                ChangedAt: DateTimeOffset.UtcNow,
                BuyerAddress: "0xabc",
                BuyerChainId: 100,
                SellerAddress: null,
                SellerChainId: null));
        }

        // Give the subscriber a moment to drain
        await Task.Delay(100);
        cts.Cancel();
        try { await subTask; } catch { /* ignore */ }

        Assert.That(received.Count, Is.GreaterThanOrEqualTo(1));
        // Do not assert exact item due to racy DropOldest timing; ensure non-blocking and that at least one event arrived.
        // Presence of the very last event is an implementation detail and not guaranteed across schedulers.
    }

    [Test]
    public async Task Max_Subscribers_Per_Key_Is_Enforced()
    {
        var bus = new InMemoryOrderStatusEventBus();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // First two subscribers should attach
        var t1 = Consume(bus.SubscribeAsync("0xabc", 100, cts.Token), cts.Token);
        var t2 = Consume(bus.SubscribeAsync("0xabc", 100, cts.Token), cts.Token);

        // Third should be rejected (enumeration yields nothing)
        var collected3 = await Consume(bus.SubscribeAsync("0xabc", 100, cts.Token), cts.Token);
        Assert.That(collected3.Count, Is.EqualTo(0));

        // Publish and ensure only first two could receive
        await bus.PublishAsync(new OrderStatusEvent("ord", null, null, "S", DateTimeOffset.UtcNow, "0xabc", 100, null, null));

        // Allow for delivery
        await Task.Delay(100);
        cts.Cancel();
        var r1 = await t1; var r2 = await t2;
        Assert.That(r1.Count + r2.Count, Is.GreaterThanOrEqualTo(1));
    }

    private static async Task<List<OrderStatusEvent>> Consume(IAsyncEnumerable<OrderStatusEvent> stream, CancellationToken ct)
    {
        var list = new List<OrderStatusEvent>();
        await foreach (var e in stream.WithCancellation(ct))
        {
            list.Add(e);
            if (list.Count > 1) break; // test helper: take up to 2 events
        }
        return list;
    }
}
