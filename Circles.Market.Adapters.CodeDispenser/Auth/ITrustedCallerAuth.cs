namespace Circles.Market.Adapters.CodeDispenser.Auth;

public interface ITrustedCallerAuth
{
    Task<TrustedCallerAuthResult> AuthorizeAsync(
        string? rawApiKey,
        string requiredScope,
        long chainId,
        string seller,
        CancellationToken ct);
}
