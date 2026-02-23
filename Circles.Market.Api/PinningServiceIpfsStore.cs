using System.Text.Json;
using System.Text.RegularExpressions;
using Circles.Profiles.Interfaces;
using Circles.Profiles.Models;

namespace Circles.Market.Api;

/// <summary>
/// IIpfsStore backed by the profile_pinning_service HTTP API.
/// Replaces direct Kubo RPC calls -- all IPFS operations proxy through
/// the pinning service which handles Filebase pinning, DB storage, and GC.
/// </summary>
public sealed class PinningServiceIpfsStore : IIpfsStore, IAsyncDisposable
{
    private static readonly Regex CidV0Pattern = new(@"^Qm[1-9A-HJ-NP-Za-km-z]{44}$", RegexOptions.Compiled);
    private static readonly Regex CidV1Pattern = new(@"^b[a-z2-7]{50,}$", RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly bool _ownsClient;

    public PinningServiceIpfsStore(string baseUrl, HttpClient? httpClient = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _ownsClient = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<string> AddStringAsync(string json, bool pin = true, CancellationToken ct = default)
    {
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync($"{_baseUrl}/pin", content, ct);
        resp.EnsureSuccessStatusCode();
        return await ExtractCidAsync(resp, ct);
    }

    public async Task<string> AddBytesAsync(ReadOnlyMemory<byte> bytes, bool pin = true, CancellationToken ct = default)
    {
        using var content = new ByteArrayContent(bytes.ToArray());
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var resp = await _http.PostAsync($"{_baseUrl}/pin-media", content, ct);
        resp.EnsureSuccessStatusCode();
        return await ExtractCidAsync(resp, ct);
    }

    public async Task<Stream> CatAsync(string cid, CancellationToken ct = default)
    {
        ValidateCid(cid);
        using var resp = await _http.GetAsync(
            $"{_baseUrl}/raw/{Uri.EscapeDataString(cid)}",
            HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var ms = new MemoryStream();
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[8192];
        int totalRead = 0;
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer, ct)) > 0)
        {
            totalRead += bytesRead;
            if (totalRead > ProtocolLimits.MaxObjectBytes)
                throw new InvalidOperationException(
                    $"Response exceeded protocol limit of {ProtocolLimits.MaxObjectBytes} bytes");
            ms.Write(buffer, 0, bytesRead);
        }
        ms.Position = 0;
        return ms;
    }

    public async Task<string> CatStringAsync(string cid, CancellationToken ct = default)
    {
        ValidateCid(cid);
        using var resp = await _http.GetAsync(
            $"{_baseUrl}/raw/{Uri.EscapeDataString(cid)}",
            HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        // Read with size guard
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var chars = new char[8192];
        var sb = new System.Text.StringBuilder();
        int totalChars = 0;
        int charsRead;
        while ((charsRead = await reader.ReadAsync(chars, ct)) > 0)
        {
            totalChars += charsRead;
            if (totalChars * 2 > ProtocolLimits.MaxObjectBytes) // rough byte estimate
                throw new InvalidOperationException(
                    $"Response exceeded protocol limit of {ProtocolLimits.MaxObjectBytes} bytes");
            sb.Append(chars, 0, charsRead);
        }
        return sb.ToString();
    }

    public Task<string> CalcCidAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
        => throw new NotSupportedException("CalcCidAsync is not supported via pinning service.");

    public Task<string> PinCidAsync(string cid, CancellationToken ct = default)
        => throw new NotSupportedException("PinCidAsync is not supported via pinning service.");

    private static void ValidateCid(string cid)
    {
        if (string.IsNullOrWhiteSpace(cid) ||
            (!CidV0Pattern.IsMatch(cid) && !CidV1Pattern.IsMatch(cid)))
            throw new ArgumentException($"Invalid CID format: {cid}", nameof(cid));
    }

    private static async Task<string> ExtractCidAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        // Pin responses are small JSON -- cap at 4 KB
        var bodyBytes = await resp.Content.ReadAsByteArrayAsync(ct);
        if (bodyBytes.Length > 4096)
            throw new InvalidOperationException("Unexpectedly large pin response");
        var body = System.Text.Encoding.UTF8.GetString(bodyBytes);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("cid").GetString()
               ?? throw new InvalidOperationException("Pinning service returned null CID");
    }

    public ValueTask DisposeAsync()
    {
        if (_ownsClient) _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
