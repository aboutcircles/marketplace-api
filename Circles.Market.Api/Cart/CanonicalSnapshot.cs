namespace Circles.Market.Api.Cart;

internal sealed record CanonicalSnapshot(Basket Basket, DateTimeOffset FetchedAt);
