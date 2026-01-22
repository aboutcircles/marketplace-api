using System.Text.Json.Serialization;

namespace Circles.Market.Api.Auth;

/// <summary>
/// Request to generate a SIWE-style challenge message for an address on a given chain.
/// </summary>
public sealed class ChallengeRequest
{
    [JsonPropertyName("address")] public string Address { get; set; } = string.Empty;
    [JsonPropertyName("chainId")] public long ChainId { get; set; } = MarketConstants.Defaults.ChainId;
    [JsonPropertyName("statement")] public string? Statement { get; set; }
    [JsonPropertyName("expirationMinutes")] public int? ExpirationMinutes { get; set; }
}

/// <summary>
/// Response containing the issued challenge message and metadata.
/// </summary>
public sealed class ChallengeResponse
{
    [JsonPropertyName("challengeId")] public Guid ChallengeId { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
    [JsonPropertyName("nonce")] public string Nonce { get; set; } = string.Empty;
    [JsonPropertyName("expiresAt")] public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>
/// Request to verify a previously issued challenge using a wallet signature.
/// </summary>
public sealed class VerifyRequest
{
    [JsonPropertyName("challengeId")] public Guid ChallengeId { get; set; }
    [JsonPropertyName("signature")] public string Signature { get; set; } = string.Empty;
}

/// <summary>
/// Result of a successful verification, including a bearer token for subsequent calls.
/// </summary>
public sealed class VerifyResponse
{
    [JsonPropertyName("token")] public string Token { get; set; } = string.Empty;
    [JsonPropertyName("address")] public string Address { get; set; } = string.Empty;
    [JsonPropertyName("chainId")] public long ChainId { get; set; }
    [JsonPropertyName("expiresIn")] public int ExpiresIn { get; set; }
}
