using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Circles.Market.Adapters.CodeDispenser;

public sealed class CodeDispenserSettings
{
    // Connection string to Postgres (can also be sourced from env POSTGRES_CONNECTION)
    public string? PostgresConnection { get; set; }

    // Optional directory to seed code pools: files named <poolId>.txt, one code per line
    public string? PoolsDir { get; set; }
}

public sealed class CodeMappingOptions
{
    public List<CodeMappingEntry> Entries { get; init; } = new();
}

public sealed class CodeMappingEntry
{
    public long ChainId { get; init; }
    public string Seller { get; init; } = string.Empty;
    public string Sku { get; init; } = string.Empty;
    public string PoolId { get; init; } = string.Empty;
    public string? DownloadUrlTemplate { get; init; }
}

public interface ICodeMappingResolver
{
    bool TryResolve(long chainId, string sellerAddress, string sku, out CodeMappingEntry? entry);
}

public sealed class ConfigCodeMappingResolver : ICodeMappingResolver
{
    private readonly Dictionary<(long chainId, string seller, string sku), CodeMappingEntry> _map;

    private static readonly Regex HexAddress = new Regex("^0x[0-9a-f]{40}$", RegexOptions.Compiled);

    public ConfigCodeMappingResolver(IOptions<CodeMappingOptions> options)
    {
        var tmp = new Dictionary<(long, string, string), CodeMappingEntry>();
        foreach (var e in options.Value.Entries)
        {
            if (string.IsNullOrWhiteSpace(e.Seller)) continue;
            if (string.IsNullOrWhiteSpace(e.Sku)) continue;
            if (string.IsNullOrWhiteSpace(e.PoolId)) continue;

            var seller = e.Seller.Trim().ToLowerInvariant();
            var sku = e.Sku.Trim().ToLowerInvariant();
            if (!HexAddress.IsMatch(seller))
            {
                throw new InvalidOperationException($"Invalid seller address in mapping: {e.Seller}");
            }
            var normalized = new CodeMappingEntry
            {
                ChainId = e.ChainId,
                Seller = seller,
                Sku = sku,
                PoolId = e.PoolId,
                DownloadUrlTemplate = e.DownloadUrlTemplate
            };
            tmp[(e.ChainId, seller, sku)] = normalized;
        }
        _map = tmp;
    }

    public bool TryResolve(long chainId, string sellerAddress, string sku, out CodeMappingEntry? entry)
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(sellerAddress) || string.IsNullOrWhiteSpace(sku))
            return false;
        var seller = sellerAddress.Trim().ToLowerInvariant();
        var skuNorm = sku.Trim().ToLowerInvariant();
        if (!HexAddress.IsMatch(seller)) return false;
        if (_map.TryGetValue((chainId, seller, skuNorm), out var found))
        {
            entry = found;
            return true;
        }
        return false;
    }
}
