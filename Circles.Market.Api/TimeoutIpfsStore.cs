using Circles.Profiles.Interfaces;

namespace Circles.Market.Api;

/// <summary>
/// IIpfsStore decorator that applies a timeout to read operations (CatAsync, CatStringAsync).
/// Prevents aggregation from stalling on slow/unresponsive IPFS fetches.
/// </summary>
public sealed class TimeoutIpfsStore : IIpfsStore
{
    private readonly IIpfsStore _inner;
    private readonly TimeSpan _timeout;

    public TimeoutIpfsStore(IIpfsStore inner, TimeSpan timeout)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _timeout = timeout;
    }

    public Task<string> AddStringAsync(string json, bool pin = true, CancellationToken ct = default)
        => _inner.AddStringAsync(json, pin, ct);

    public Task<string> AddBytesAsync(ReadOnlyMemory<byte> bytes, bool pin = true, CancellationToken ct = default)
        => _inner.AddBytesAsync(bytes, pin, ct);

    public async Task<Stream> CatAsync(string cid, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        try
        {
            return await _inner.CatAsync(cid, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"CatAsync for CID '{cid}' exceeded {_timeout.TotalMilliseconds}ms");
        }
    }

    public async Task<string> CatStringAsync(string cid, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        try
        {
            return await _inner.CatStringAsync(cid, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"CatStringAsync for CID '{cid}' exceeded {_timeout.TotalMilliseconds}ms");
        }
    }

    public Task<string> CalcCidAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
        => _inner.CalcCidAsync(bytes, ct);

    public Task<string> PinCidAsync(string cid, CancellationToken ct = default)
        => _inner.PinCidAsync(cid, ct);
}
