namespace Circles.Market.Adapters.CodeDispenser;

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
