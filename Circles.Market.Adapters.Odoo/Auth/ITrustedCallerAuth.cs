namespace Circles.Market.Adapters.Odoo.Auth;

public sealed class TrustedCallerAuthResult
{
    public bool Allowed { get; init; }
    public string? CallerId { get; init; }
    public string? Reason { get; init; }
}

public interface ITrustedCallerAuth
{
    Task<TrustedCallerAuthResult> AuthorizeAsync(
        string? rawApiKey,
        string requiredScope,
        long chainId,
        string seller,
        CancellationToken ct);
}
