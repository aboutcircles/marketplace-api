namespace Circles.Market.Auth.Siwe;

public sealed class AuthChallenge
{
    public Guid Id { get; set; }
    public string Address { get; set; } = string.Empty;
    public long ChainId { get; set; }
    public string Nonce { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public string? UserAgent { get; set; }
    public string? Ip { get; set; }
}

public interface IAuthChallengeStore
{
    Task SaveAsync(AuthChallenge ch, CancellationToken ct = default);
    Task<AuthChallenge?> GetAsync(Guid id, CancellationToken ct = default);
    Task<bool> TryMarkUsedAsync(Guid id, CancellationToken ct = default);
}

public interface ITokenService
{
    string Issue(TokenSubject subject, TimeSpan lifetime);
}

public readonly record struct TokenSubject(string Address, long ChainId)
{
    public string Sub => $"{Address.ToLowerInvariant()}@{ChainId}";
}