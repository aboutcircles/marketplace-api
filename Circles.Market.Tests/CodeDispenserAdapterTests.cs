using Circles.Market.Adapters.CodeDispenser;
using Microsoft.Extensions.Options;

namespace Circles.Market.Tests;

public class InMemoryStore : ICodeDispenserStore
{
    private readonly Dictionary<string, Queue<string>> _pools = new();
    private readonly Dictionary<(long chainId,string seller,string paymentRef,string poolId,string sku), List<string>> _assigned = new();

    public Task EnsureSchemaAsync(CancellationToken ct) => Task.CompletedTask;

    public Task SeedFromDirAsync(string? poolsDir, CancellationToken ct) => Task.CompletedTask;

    public Task<(AssignmentStatus status, string? code)> AssignAsync(long chainId, string seller, string paymentReference, string orderId, string sku, string poolId, CancellationToken ct)
    {
        var key = (chainId, seller, paymentReference, poolId, sku);
        if (_assigned.TryGetValue(key, out var existingList) && existingList.Count > 0)
            return Task.FromResult((AssignmentStatus.Ok, (string?)existingList[0]));

        if (!_pools.TryGetValue(poolId, out var q) || q.Count == 0)
            return Task.FromResult((AssignmentStatus.Depleted, (string?)null));

        var code = q.Dequeue();
        _assigned[key] = new List<string> { code };
        return Task.FromResult((AssignmentStatus.Ok, (string?)code));
    }

    public Task<(AssignmentStatus status, List<string> codes)> AssignManyAsync(long chainId, string seller, string paymentReference, string orderId, string sku, string poolId, int quantity, CancellationToken ct)
    {
        if (quantity <= 0) quantity = 1;
        var key = (chainId, seller, paymentReference, poolId, sku);
        if (!_assigned.TryGetValue(key, out var list))
        {
            list = new List<string>();
            _assigned[key] = list;
        }
        // If we already have enough, return those
        if (list.Count >= quantity)
        {
            return Task.FromResult((AssignmentStatus.Ok, list.Take(quantity).ToList()));
        }
        // Else, pull from pool
        int toAssign = quantity - list.Count;
        if (!_pools.TryGetValue(poolId, out var q))
        {
            q = new Queue<string>();
            _pools[poolId] = q;
        }
        for (int i = 0; i < toAssign && q.Count > 0; i++)
        {
            list.Add(q.Dequeue());
        }
        if (list.Count == 0)
        {
            return Task.FromResult((AssignmentStatus.Depleted, list));
        }
        return Task.FromResult((AssignmentStatus.Ok, list.Take(quantity).ToList()));
    }

    public Task<long> GetRemainingAsync(string poolId, CancellationToken ct)
    {
        if (_pools.TryGetValue(poolId, out var q)) return Task.FromResult((long)q.Count);
        return Task.FromResult(0L);
    }

    public void Seed(string poolId, params string[] codes)
    {
        if (!_pools.TryGetValue(poolId, out var q))
        {
            q = new Queue<string>();
            _pools[poolId] = q;
        }
        foreach (var c in codes) q.Enqueue(c);
    }
}

[TestFixture]
public class CodeDispenserAdapterTests
{
    [Test]
    public void Mapping_Normalization_Lowercases_Seller_And_Sku()
    {
        var (seller, sku) = PostgresCodeMappingResolver.Normalize("0xAaBbCcDdEeFf00112233445566778899Aabbccdd", "TeE-bLaCk");
        Assert.That(seller, Is.EqualTo("0xaabbccddeeff00112233445566778899aabbccdd"));
        Assert.That(sku, Is.EqualTo("tee-black"));
    }

    [Test]
    public async Task Idempotency_Same_PaymentReference_Returns_Same_Code()
    {
        var store = new InMemoryStore();
        store.Seed("poolA", "CODE1", "CODE2");
        var res1 = await store.AssignAsync(100, "0xaabbccddeeff00112233445566778899aabbccdd", "pay1", "ord1", "tee", "poolA", CancellationToken.None);
        var res2 = await store.AssignAsync(100, "0xaabbccddeeff00112233445566778899aabbccdd", "pay1", "ord1", "tee", "poolA", CancellationToken.None);
        Assert.That(res1.status, Is.EqualTo(AssignmentStatus.Ok));
        Assert.That(res2.status, Is.EqualTo(AssignmentStatus.Ok));
        Assert.That(res1.code, Is.EqualTo(res2.code));
    }

    [Test]
    public async Task Depletion_Empty_Pool_Returns_Depleted()
    {
        var store = new InMemoryStore();
        var res = await store.AssignAsync(100, "0xaabbccddeeff00112233445566778899aabbccdd", "p1", "o1", "tee", "poolA", CancellationToken.None);
        Assert.That(res.status, Is.EqualTo(AssignmentStatus.Depleted));
    }

    [Test]
    public void NotApplicable_When_No_Mapping()
    {
        var opts = Options.Create(new CodeMappingOptions { Entries = new List<CodeMappingEntry>() });
        var resolver = new ConfigCodeMappingResolver(opts);
        bool ok = resolver.TryResolve(1229, "0xaabbccddeeff00112233445566778899aabbccdd", "unknown", out var entry);
        Assert.That(ok, Is.False);
        Assert.That(entry, Is.Null);
    }
}
