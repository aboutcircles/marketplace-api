namespace Circles.Market.Api.Inventory;

/// <summary>
/// Store for tracking one-off sales to implement sold-once default availability.
/// </summary>
public interface IOneOffSalesStore
{
    /// <summary>
    /// Checks if a one-off item has already been sold.
    /// </summary>
    /// <param name="chainId">The blockchain chain ID</param>
    /// <param name="seller">The seller address (normalized to lowercase)</param>
    /// <param name="sku">The product SKU (normalized to lowercase)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if the item has been sold, false otherwise</returns>
    Task<bool> IsSoldAsync(long chainId, string seller, string sku, CancellationToken ct);
}
