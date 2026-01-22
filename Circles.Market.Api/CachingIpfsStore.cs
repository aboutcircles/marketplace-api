using System.Text;
using Circles.Profiles.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Circles.Market.Api;

public sealed class CachingIpfsStore : IIpfsStore
{
    // Shared protocol limit for object size (8 MiB)
    private static int MaxObjectBytes => Circles.Profiles.Models.ProtocolLimits.MaxObjectBytes;

    private sealed class RefGate
    {
        public readonly SemaphoreSlim Sem = new(1, 1);
        public int RefCount;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, RefGate> Gates = new();
    private static readonly int MaxGates = int.TryParse(Environment.GetEnvironmentVariable("IPFS_GATES_MAX"), out var v) && v > 0 ? v : 20000;

    private readonly IIpfsStore _inner;
    private readonly IMemoryCache _cache;
    private readonly ILogger<CachingIpfsStore> _log;

    public CachingIpfsStore(IIpfsStore inner, IMemoryCache cache, ILogger<CachingIpfsStore> log)
    {
        _inner = inner;
        _cache = cache;
        _log = log;
    }

    private sealed record CacheEntry(byte[] Bytes);

    public async Task<string> AddStringAsync(string json, bool pin = true, CancellationToken ct = default) =>
        await _inner.AddStringAsync(json, pin, ct);

    public async Task<string> AddBytesAsync(ReadOnlyMemory<byte> bytes, bool pin = true, CancellationToken ct = default) =>
        await _inner.AddBytesAsync(bytes, pin, ct);

    public async Task<Stream> CatAsync(string cid, CancellationToken ct = default)
    {
        // Log every Cat request; cache state will follow
        _log.LogDebug("IPFS Cat requested for CID {Cid}", cid);

        bool cachedHit = _cache.TryGetValue<CacheEntry>(cid, out var entry);
        if (cachedHit)
        {
            _log.LogDebug("IPFS Cat cache hit for CID {Cid} (bytes={Length})", cid, entry!.Bytes.Length);
            return new MemoryStream(entry!.Bytes, writable: false);
        }

        var gate = Gates.GetOrAdd(cid, _ => new RefGate());
        Interlocked.Increment(ref gate.RefCount);

        bool acquired = false;
        try
        {
            await gate.Sem.WaitAsync(ct);
            acquired = true;

            bool cachedHit2 = _cache.TryGetValue<CacheEntry>(cid, out entry);
            if (cachedHit2)
            {
                _log.LogDebug("IPFS Cat cache hit (post-wait) for CID {Cid} (bytes={Length})", cid, entry!.Bytes.Length);
                return new MemoryStream(entry!.Bytes, writable: false);
            }

            await using var s = await _inner.CatAsync(cid, ct);
            using var ms = new MemoryStream();
            await s.CopyToAsync(ms, ct);
            var bytes = ms.ToArray();

            // Enforce protocol limit strictly on payload size only
            if (bytes.Length > MaxObjectBytes)
            {
                _log.LogWarning("IPFS object too large for protocol: CID {Cid} (bytes={Length}, cap={Cap})", cid, bytes.Length, MaxObjectBytes);
                throw new PayloadTooLargeException();
            }

            int sizeWithOverhead = bytes.Length + 128; // overhead for cache sizing only

            var opts = new MemoryCacheEntryOptions()
                .SetSize(sizeWithOverhead)
                .SetPriority(CacheItemPriority.Low)
                .SetSlidingExpiration(TimeSpan.FromMinutes(30));

            _cache.Set(cid, new CacheEntry(bytes), opts);
            _log.LogDebug("IPFS Cat fetched CID {Cid} (bytes={Length}) and cached", cid, bytes.Length);
            return new MemoryStream(bytes, writable: false);
        }
        finally
        {
            if (acquired)
            {
                gate.Sem.Release();
            }
            int refs = Interlocked.Decrement(ref gate.RefCount);
            if (refs == 0)
            {
                if (Gates.Count > MaxGates)
                {
                    Gates.TryRemove(cid, out _);
                }
            }
        }
    }

    public async Task<string> CatStringAsync(string cid, CancellationToken ct = default)
    {
        await using var s = await CatAsync(cid, ct);
        using var sr = new StreamReader(s, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: false);
        return await sr.ReadToEndAsync(ct);
    }

    public Task<string> CalcCidAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default) =>
        _inner.CalcCidAsync(bytes, ct);

    public Task<string> PinCidAsync(string cid, CancellationToken ct = default) =>
        _inner.PinCidAsync(cid, ct);
}
