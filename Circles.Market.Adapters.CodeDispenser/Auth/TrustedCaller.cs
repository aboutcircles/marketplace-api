using System;

namespace Circles.Market.Adapters.CodeDispenser.Auth;

public sealed class TrustedCaller
{
    public string CallerId { get; init; } = string.Empty;
    public byte[] ApiKeySha256 { get; init; } = Array.Empty<byte>();
    public string[] Scopes { get; init; } = Array.Empty<string>();
    public string? SellerAddress { get; init; }
    public long? ChainId { get; init; }
    public bool Enabled { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
}

public sealed class TrustedCallerAuthResult
{
    public bool Allowed { get; init; }
    public string? CallerId { get; init; }
    public string? Reason { get; init; }
}