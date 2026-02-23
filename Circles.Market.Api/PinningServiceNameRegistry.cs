using System.Text.Json;
using System.Text.RegularExpressions;
using Circles.Profiles.Interfaces;

namespace Circles.Market.Api;

/// <summary>
/// INameRegistry backed by the profile_pinning_service HTTP API.
/// Resolves avatar address -> profile CID via the pinning service's
/// indexed profile database instead of on-chain getMetadataDigest calls.
/// </summary>
public sealed class PinningServiceNameRegistry : INameRegistry, IDisposable
{
    private static readonly Regex AddressPattern = new(@"^0x[0-9a-fA-F]{40}$", RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly bool _ownsClient;

    public PinningServiceNameRegistry(string baseUrl, HttpClient? httpClient = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _ownsClient = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task<string?> GetProfileCidAsync(string avatar, CancellationToken ct = default)
    {
        if (!AddressPattern.IsMatch(avatar))
            throw new ArgumentException($"Invalid Ethereum address: {avatar}", nameof(avatar));

        using var resp = await _http.GetAsync(
            $"{_baseUrl}/profile/{Uri.EscapeDataString(avatar)}", ct);

        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        resp.EnsureSuccessStatusCode();

        var bodyBytes = await resp.Content.ReadAsByteArrayAsync(ct);
        if (bodyBytes.Length > 8192)
            throw new InvalidOperationException("Unexpectedly large profile response");

        var body = System.Text.Encoding.UTF8.GetString(bodyBytes);
        using var doc = JsonDocument.Parse(body);

        // Pinning service returns { "CID": "Qm...", "name": ..., ... }
        if (doc.RootElement.TryGetProperty("CID", out var cidProp))
            return cidProp.GetString();

        return null;
    }

    public Task<string?> UpdateProfileCidAsync(string avatar, byte[] metadataDigest32, CancellationToken ct = default)
        => throw new NotSupportedException(
            "UpdateProfileCidAsync is not supported via pinning service.");

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
