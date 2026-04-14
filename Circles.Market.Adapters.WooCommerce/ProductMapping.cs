namespace Circles.Market.Adapters.WooCommerce;

/// <summary>
/// Lightweight DTO returned by <see cref="IProductMappingResolver"/>.
/// </summary>
public sealed class ProductMappingInfo
{
    public required string Sku { get; init; }
    public required string WcProductSku { get; init; }
    public int? WcProductId { get; init; }
}

/// <summary>
/// A single row from the <c>wc_product_mappings</c> table.
/// </summary>
public sealed class ProductMappingEntry
{
    public Guid Id { get; init; }
    public long ChainId { get; init; }
    public string SellerAddress { get; init; } = string.Empty;
    public required string Sku { get; init; }
    /// <summary>Required WooCommerce product SKU.</summary>
    public required string WcProductSku { get; init; }
    /// <summary>Optional cached WooCommerce product ID (preferred over SKU lookup).</summary>
    public int? WcProductId { get; init; }
    public bool Enabled { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
}

