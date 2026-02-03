namespace Circles.Market.Shared.Admin;

public sealed class AdminAuthChallenge
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

public interface IAdminTokenService
{
    string Issue(AdminTokenSubject subject, TimeSpan lifetime);
}

public readonly record struct AdminTokenSubject(string Address, long ChainId)
{
    public string Sub => $"{Address.ToLowerInvariant()}@{ChainId}";
}
