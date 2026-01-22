using System;

namespace Circles.Market.Api.Cart;

/// <summary>
/// Exception thrown when attempting to purchase a one-off item that has already been sold.
/// This maps to HTTP 409 Conflict to indicate someone else already bought it.
/// </summary>
public class OneOffAlreadySoldException : Exception
{
    public long ChainId { get; }
    public string Seller { get; }
    public string Sku { get; }

    public OneOffAlreadySoldException(long chainId, string seller, string sku)
        : base($"One-off item already sold: chainId={chainId}, seller={seller}, sku={sku}")
    {
        ChainId = chainId;
        Seller = seller;
        Sku = sku;
    }

    public OneOffAlreadySoldException(long chainId, string seller, string sku, Exception innerException)
        : base($"One-off item already sold: chainId={chainId}, seller={seller}, sku={sku}", innerException)
    {
        ChainId = chainId;
        Seller = seller;
        Sku = sku;
    }
}
