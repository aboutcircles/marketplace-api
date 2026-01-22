using Circles.Market.Api;
using Circles.Profiles.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Circles.Market.Tests;

[TestFixture]
public class CachingIpfsStoreTests
{
    private sealed class FakeInnerStore : IIpfsStore
    {
        private readonly byte[] _bytes;
        public int CatCalls { get; private set; }
        public FakeInnerStore(byte[] bytes) { _bytes = bytes; }
        public Task<string> AddStringAsync(string json, bool pin = true, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> AddBytesAsync(ReadOnlyMemory<byte> bytes, bool pin = true, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Stream> CatAsync(string cid, CancellationToken ct = default)
        {
            CatCalls++;
            return Task.FromResult<Stream>(new MemoryStream(_bytes, writable: false));
        }
        public Task<string> CatStringAsync(string cid, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> CalcCidAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> PinCidAsync(string cid, CancellationToken ct = default) => throw new NotImplementedException();
    }

    [Test]
    public async Task CatAsync_ExactlyAtLimit_DoesNotThrow_AndCaches()
    {
        int limit = Profiles.Models.ProtocolLimits.MaxObjectBytes;
        var bytes = new byte[limit];
        new Random(42).NextBytes(bytes);

        var inner = new FakeInnerStore(bytes);
        var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 200 * 1024 * 1024 });
        var sut = new CachingIpfsStore(inner, cache, NullLogger<CachingIpfsStore>.Instance);

        // First call: fetches from inner and caches
        await using (var s1 = await sut.CatAsync("QmDummy", CancellationToken.None))
        {
            using var ms1 = new MemoryStream();
            await s1.CopyToAsync(ms1);
            Assert.That(ms1.Length, Is.EqualTo(limit));
        }

        // Second call: should be served from cache (no extra inner call ideally)
        await using (var s2 = await sut.CatAsync("QmDummy", CancellationToken.None))
        {
            using var ms2 = new MemoryStream();
            await s2.CopyToAsync(ms2);
            Assert.That(ms2.Length, Is.EqualTo(limit));
        }

        Assert.That(inner.CatCalls, Is.EqualTo(1));
    }
}
