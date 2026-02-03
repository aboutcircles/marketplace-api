using System.Text.Json.Serialization;

namespace Circles.Market.Shared.Admin;

public sealed class AdminChallengeRequest
{
    [JsonPropertyName("address")] public string Address { get; set; } = string.Empty;
    [JsonPropertyName("chainId")] public long ChainId { get; set; }
    [JsonPropertyName("statement")] public string? Statement { get; set; }
    [JsonPropertyName("expirationMinutes")] public int? ExpirationMinutes { get; set; }
}

public sealed class AdminChallengeResponse
{
    [JsonPropertyName("challengeId")] public Guid ChallengeId { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
    [JsonPropertyName("nonce")] public string Nonce { get; set; } = string.Empty;
    [JsonPropertyName("expiresAt")] public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class AdminVerifyRequest
{
    [JsonPropertyName("challengeId")] public Guid ChallengeId { get; set; }
    [JsonPropertyName("signature")] public string Signature { get; set; } = string.Empty;
}

public sealed class AdminVerifyResponse
{
    [JsonPropertyName("token")] public string Token { get; set; } = string.Empty;
    [JsonPropertyName("address")] public string Address { get; set; } = string.Empty;
    [JsonPropertyName("chainId")] public long ChainId { get; set; }
    [JsonPropertyName("expiresIn")] public int ExpiresIn { get; set; }
}
