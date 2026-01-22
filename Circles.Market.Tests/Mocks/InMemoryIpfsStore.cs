using System.Security.Cryptography;
using System.Text;
using Circles.Profiles.Interfaces;
using Circles.Profiles.Sdk.Utils;

namespace Circles.Market.Tests.Mocks;

/// <summary>A throw‑away, thread‑safe IPFS stub used by the unit‑tests.</summary>
internal sealed class InMemoryIpfsStore : IIpfsStore
{
    private readonly Dictionary<string, byte[]> _blobs = new();
    private readonly object _gate = new();

    /* ─────────────────────────── helpers ─────────────────────────── */

    // sha2‑256 multihash  →  CID‑v0 (base58btc “Qm…”)
    private static string CidFor(ReadOnlySpan<byte> bytes)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(bytes, digest); // sha2‑256

        return CidConverter.DigestToCid(digest.ToArray()); // add multihash header + Base58
    }

    /* ─────────────────────────── write API ───────────────────────── */

    public Task<string> AddStringAsync(string json, bool pin = true,
        CancellationToken ct = default) =>
        AddBytesAsync(Encoding.UTF8.GetBytes(json), pin, ct);

    public Task<string> AddBytesAsync(ReadOnlyMemory<byte> bytes, bool pin = true,
        CancellationToken ct = default)
    {
        string cid = CidFor(bytes.Span);

        if (pin)
        {
            lock (_gate) _blobs[cid] = bytes.ToArray();
        }

        return Task.FromResult(cid);
    }

    /* ─────────────────────────── read API ────────────────────────── */

    public Task<Stream> CatAsync(string cid, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_blobs.TryGetValue(cid, out var data))
                throw new KeyNotFoundException(cid);

            return Task.FromResult<Stream>(new MemoryStream(data, writable: false));
        }
    }

    public async Task<string> CatStringAsync(string cid, CancellationToken ct = default)
    {
        await using var stream = await CatAsync(cid, ct);
        using var sr = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: false);
        return await sr.ReadToEndAsync(ct);
    }

    /* ───────────────────── hash‑only helper ──────────────────────── */

    public Task<string> CalcCidAsync(ReadOnlyMemory<byte> bytes,
        CancellationToken ct = default) =>
        Task.FromResult(CidFor(bytes.Span));

    public Task<string> PinCidAsync(string cid, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_blobs.ContainsKey(cid))
            {
                throw new KeyNotFoundException($"Cannot pin unknown CID: {cid}");
            }
        }

        return Task.FromResult(cid);
    }
}
