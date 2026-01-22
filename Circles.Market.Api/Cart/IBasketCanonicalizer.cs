namespace Circles.Market.Api.Cart;

public interface IBasketCanonicalizer
{
    /// <summary>
    /// Resolves canonical product/offer data for each basket line and rewrites the basket items in-place.
    /// Throws ArgumentException/InvalidOperationException on unrecoverable issues.
    /// </summary>
    Task CanonicalizeAsync(Basket basket, CancellationToken ct = default);
}
