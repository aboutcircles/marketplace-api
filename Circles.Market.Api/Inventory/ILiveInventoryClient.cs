namespace Circles.Market.Api.Inventory;

public interface ILiveInventoryClient
{
    Task<(bool IsError, string? Error, Circles.Profiles.Models.Market.SchemaOrgQuantitativeValue? Value)> FetchInventoryAsync(
        string url,
        CancellationToken ct = default);
}
