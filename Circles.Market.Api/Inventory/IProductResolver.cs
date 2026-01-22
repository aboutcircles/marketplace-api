using Circles.Profiles.Models.Market;

namespace Circles.Market.Api.Inventory;

public interface IProductResolver
{
    Task<(SchemaOrgProduct? Product, string? Cid)> ResolveProductAsync(
        long chainId,
        string seller,
        string sku,
        CancellationToken ct = default);

    Task<(SchemaOrgProduct? Product, string? Cid)> ResolveProductAsync(
        long chainId,
        string seller,
        string? @operator,
        string sku,
        CancellationToken ct = default);
}
