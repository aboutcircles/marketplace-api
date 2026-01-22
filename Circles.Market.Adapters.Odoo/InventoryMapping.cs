using System;
using System.Collections.Generic;

namespace Circles.Market.Adapters.Odoo;

public sealed class InventoryMappingEntry
{
    public string Seller { get; set; } = string.Empty;
    public string Sku { get; set; } = string.Empty;
    public string OdooProductCode { get; set; } = string.Empty;
}

public sealed class InventoryMappingOptions
{
    public List<InventoryMappingEntry> Items { get; set; } = new();
}

public interface IInventoryMappingResolver
{
    Task<(bool Mapped, InventoryMappingEntry? Entry)> TryResolveAsync(long chainId, string sellerAddress, string sku, CancellationToken ct);
}

// Dead code: ConfigInventoryMappingResolver removed. Use PostgresInventoryMappingResolver instead.
