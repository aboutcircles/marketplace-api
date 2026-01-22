using System.Text.Json;
using Circles.Profiles.Interfaces;
using Circles.Profiles.Models.Core;
using Circles.Profiles.Models.Market;
using Circles.Profiles.Sdk;
using Circles.Profiles.Sdk.Utils;
using Nethereum.Hex.HexConvertors.Extensions;

namespace Circles.Market.Api.Inventory;

public sealed class ProductResolver : IProductResolver
{
    private readonly INameRegistry _registry;
    private readonly IIpfsStore _ipfs;
    private readonly ISignatureVerifier _verifier;

    // Chunk cache to avoid duplicate loads when many lookups hit the same chunk
    private static readonly int MaxCachedChunks = ReadPositiveIntEnv("CIRCLES_PRODUCT_RESOLVER_MAX_CACHED_CHUNKS", 256);
    private static int ReadPositiveIntEnv(string name, int fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var v) && v > 0 ? v : fallback;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<NamespaceChunk>> _chunkCache = new(StringComparer.Ordinal);

    public ProductResolver(INameRegistry registry, IIpfsStore ipfs, ISignatureVerifier verifier)
    {
        _registry = registry;
        _ipfs = ipfs;
        _verifier = verifier;
    }

    public Task<(SchemaOrgProduct? Product, string? Cid)> ResolveProductAsync(
        long chainId,
        string seller,
        string sku,
        CancellationToken ct = default)
        => ResolveProductAsync(chainId, seller, null, sku, ct);

    public async Task<(SchemaOrgProduct? Product, string? Cid)> ResolveProductAsync(
        long chainId,
        string seller,
        string? @operator,
        string sku,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(seller)) throw new ArgumentException("seller is required", nameof(seller));
        if (string.IsNullOrWhiteSpace(sku)) throw new ArgumentException("sku is required", nameof(sku));

        // Logical link name per spec
        string logicalName = $"product/{sku.Trim().ToLowerInvariant()}";
        string sellerLower = seller.Trim().ToLowerInvariant();
        string? opLower = @operator is null ? null : Utils.NormalizeAddr(@operator);

        string? profileCid = await _registry.GetProfileCidAsync(sellerLower, ct);
        if (string.IsNullOrWhiteSpace(profileCid)) return (null, null);

        // Load profile to enumerate namespaces
        Profile? profile;
        try
        {
            var profileJson = await _ipfs.CatStringAsync(profileCid, ct);
            profile = JsonSerializer.Deserialize<Profile>(profileJson, Profiles.Models.JsonSerializerOptions.JsonLd);
        }
        catch (JsonException)
        {
            // Malformed profile JSON; treat as not found
            return (null, null);
        }
        catch (InvalidDataException)
        {
            // Bad envelope/content; treat as not found
            return (null, null);
        }
        if (profile is null || profile.Namespaces.Count == 0) return (null, null);

        // Probe namespaces; stop on first matching product
        var namespaces = profile.Namespaces.AsEnumerable();
        if (opLower is not null)
        {
            namespaces = namespaces.Where(kv => string.Equals(kv.Key, opLower, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var kv in namespaces)
        {
            string indexCid = kv.Value;
            if (string.IsNullOrWhiteSpace(indexCid)) continue;

            NameIndexDoc indexDoc;
            try
            {
                indexDoc = await Helpers.LoadIndex(indexCid, _ipfs, ct);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is JsonException || ex is InvalidDataException)
            {
                // Skip invalid metadata entry or malformed JSON
                continue;
            }

            // Random access by logical name → load only the referenced chunk
            if (!indexDoc.Entries.TryGetValue(logicalName, out var chunkCid) || string.IsNullOrWhiteSpace(chunkCid))
            {
                continue;
            }

            NamespaceChunk chunk;
            try
            {
                chunk = await GetOrLoadChunkAsync(chunkCid, ct);
            }
            catch
            {
                continue;
            }

            // Scan the single chunk for the newest valid link with the desired name
            CustomDataLink? best = null;
            int bestIndex = -1;

            for (int i = 0; i < chunk.Links.Count; i++)
            {
                var l = chunk.Links[i];

                // Envelope
                try { JsonLdGuards.EnsureCustomDataLinkEnvelope(l, chunkCid); }
                catch (JsonException) { continue; }

                if (!string.Equals(l.Name, logicalName, StringComparison.OrdinalIgnoreCase)) continue;
                if (l.ChainId != chainId) continue;
                if (!IsValidNonce(l.Nonce)) continue;
                if (string.IsNullOrWhiteSpace(l.Cid)) continue;

                if (!await VerifyAsync(l, ct)) continue;
                if (!CriticalExtensionsSupported(l)) continue;

                if (best is null || l.SignedAt > best.SignedAt || (l.SignedAt == best.SignedAt && i > bestIndex))
                {
                    best = l;
                    bestIndex = i;
                }
            }

            if (best is null) continue;

            // Found the product link → fetch product JSON
            SchemaOrgProduct? product;
            try
            {
                var prodJson = await _ipfs.CatStringAsync(best.Cid!, ct);
                product = JsonSerializer.Deserialize<SchemaOrgProduct>(prodJson,
                    Profiles.Models.JsonSerializerOptions.JsonLd);
            }
            catch (JsonException)
            {
                // Malformed product JSON; skip
                continue;
            }
            catch (InvalidDataException)
            {
                // Bad content/envelope; skip
                continue;
            }
            if (product is null) continue;

            // Guard wrong payloads (e.g., Tombstone)
            if (!string.Equals(product.Type, "Product", StringComparison.Ordinal))
            {
                continue;
            }

            // Ensure sku matches the request (case-insensitive)
            if (!string.Equals(product.Sku, sku, StringComparison.OrdinalIgnoreCase)) continue;
            return (product, best.Cid);
        }

        return (null, null);
    }

    private async Task<NamespaceChunk> GetOrLoadChunkAsync(string chunkCid, CancellationToken ct)
    {
        // simple size guard: clear cache if it grows beyond MaxCachedChunks
        if (_chunkCache.Count > MaxCachedChunks)
        {
            foreach (var key in _chunkCache.Keys.Take(Math.Max(1, _chunkCache.Count - MaxCachedChunks)))
            {
                _chunkCache.TryRemove(key, out _);
            }
        }

        var task = _chunkCache.GetOrAdd(chunkCid, key => Helpers.LoadChunk(key, _ipfs, ct));
        return await task;
    }

    private static bool IsValidNonce(string? nonce)
    {
        if (string.IsNullOrWhiteSpace(nonce)) return false;
        const int expectedLength = 34; // "0x" + 32 hex chars
        if (nonce.Length != expectedLength) return false;
        if (!(nonce[0] == '0' && nonce[1] == 'x')) return false;
        for (int i = 2; i < nonce.Length; i++)
        {
            char c = nonce[i];
            bool isDigit = c >= '0' && c <= '9';
            bool isLowerHex = c >= 'a' && c <= 'f';
            if (!isDigit && !isLowerHex) return false;
        }
        return true;
    }

    private async Task<bool> VerifyAsync(CustomDataLink l, CancellationToken ct)
    {
        byte[] payloadBytes;
        try
        {
            payloadBytes = CanonicalJson.CanonicaliseWithoutSignature(l);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return false;
        }

        byte[] payloadHash = Sha3.Keccak256Bytes(payloadBytes);

        byte[] signature;
        try
        {
            signature = l.Signature.HexToByteArray();
        }
        catch (Exception ex) when (ex is FormatException or ArgumentNullException)
        {
            return false;
        }
        if (signature.Length != 65) { return false; }

        bool primaryOk;
        try
        {
            primaryOk = await _verifier.VerifyAsync(payloadHash, l.SignerAddress, signature, ct);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (primaryOk) return true;

        if (_verifier is ISafeBytesVerifier safe)
        {
            bool bytesOk;
            try
            {
                bytesOk = await safe.Verify1271WithBytesAsync(payloadBytes, l.SignerAddress, signature, ct);
            }
            catch (ArgumentException)
            {
                return false;
            }
            if (bytesOk) return true;
        }

        return false;
    }

    private static bool CriticalExtensionsSupported(CustomDataLink link)
    {
        if (link.ExtensionData is null) return true;
        if (!link.ExtensionData.TryGetValue("criticalExtensions", out var critElement)) return true;
        if (critElement.ValueKind == JsonValueKind.Null) return true;
        if (critElement.ValueKind != JsonValueKind.Array) return false;
        return critElement.GetArrayLength() == 0;
    }
}
