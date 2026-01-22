using Microsoft.Extensions.Caching.Memory;

namespace Circles.Market.Api.Auth;

public interface IOutboundServiceAuthProvider
{
    Task<(string headerName, string apiKey)?> TryGetHeaderAsync(
        Uri endpoint,
        string serviceKind, // 'fulfillment' | 'inventory'
        string? sellerAddress,
        long chainId,
        CancellationToken ct = default);
}
