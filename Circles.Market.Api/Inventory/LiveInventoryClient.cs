using System.Text.Json;
using System.Net;
using Circles.Profiles.Models.Market;
using Circles.Market.Api.Outbound;

namespace Circles.Market.Api.Inventory;

internal sealed class LiveInventoryClient : ILiveInventoryClient
{
    private readonly IHttpClientFactory _http;
    private readonly Circles.Market.Api.Auth.IOutboundServiceAuthProvider _authProvider;

    public LiveInventoryClient(IHttpClientFactory http, Circles.Market.Api.Auth.IOutboundServiceAuthProvider authProvider)
    {
        _http = http;
        _authProvider = authProvider;
    }

    public async Task<(bool IsError, string? Error, SchemaOrgQuantitativeValue? Value)> FetchInventoryAsync(
        string url,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !OutboundGuards.IsHttpOrHttps(uri))
        {
            return (true, "Invalid or non-HTTP/HTTPS URL", null);
        }

        // Very basic hardening: short timeout budget and a single retry on transient errors
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(OutboundGuards.GetInventoryTimeoutMs()));

        var (seller, chain) = TryParseInventoryPath(uri);
        (string headerName, string apiKey)? header = null;
        if (seller != null && chain > 0)
        {
            header = await _authProvider.TryGetHeaderAsync(uri, serviceKind: "inventory", sellerAddress: seller, chainId: chain, ct: timeoutCts.Token);
        }

        if (header == null && await OutboundGuards.IsPrivateOrLocalTargetAsync(uri, timeoutCts.Token))
        {
            return (true, "Blocked private address", null);
        }

        using var client = _http.CreateClient(header != null ? "inventory_trusted" : "inventory_public");

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                HttpResponseMessage resp;
                if (header != null)
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                    req.Headers.TryAddWithoutValidation(OutboundGuards.HopHeader, "1");
                    req.Headers.TryAddWithoutValidation(header.Value.headerName, header.Value.apiKey);
                    resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
                }
                else
                {
                    using var initialReq = new HttpRequestMessage(HttpMethod.Get, uri);
                    initialReq.Headers.TryAddWithoutValidation(OutboundGuards.HopHeader, "1");
                    resp = await OutboundGuards.SendWithRedirectsAsync(client, initialReq, OutboundGuards.GetMaxRedirects(), u => new HttpRequestMessage(HttpMethod.Get, u)
                    {
                        Headers = { { OutboundGuards.HopHeader, "1" } }
                    }, timeoutCts.Token);
                }

                using (resp)
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        return (true, $"HTTP {(int)resp.StatusCode}", null);
                    }

                    var bytes = await OutboundGuards.ReadWithLimitAsync(resp.Content, OutboundGuards.GetMaxResponseBytes(), timeoutCts.Token);
                    using var elDoc = JsonDocument.Parse(bytes);
                    var el = elDoc.RootElement;

                    if (el.ValueKind != JsonValueKind.Object)
                    {
                        return (true, "inventoryFeed must be a QuantitativeValue object", null);
                    }

                    try
                    {
                        var qv = el.Deserialize<SchemaOrgQuantitativeValue>(
                            Circles.Profiles.Models.JsonSerializerOptions.JsonLd)!;

                        if (!string.Equals(qv.Type, "QuantitativeValue", StringComparison.Ordinal))
                        {
                            return (true, "@type must be QuantitativeValue", null);
                        }

                        // value is a long in the model; fractional values are not representable.

                        return (false, null, qv);
                    }
                    catch (Exception ex)
                    {
                        return (true, $"Invalid QuantitativeValue: {ex.Message}", null);
                    }
                }
            }
            catch (HttpRequestException) when (attempt == 0)
            {
                // retry once on transient network errors
                await Task.Delay(100, CancellationToken.None);
                continue;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt == 0)
            {
                // local timeout hit; retry once quickly
                continue;
            }
        }

        return (true, "Inventory request failed after retry", null);
    }

    private static (string? seller, long chainId) TryParseInventoryPath(Uri uri)
    {
        try
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            // expect: (inventory|availability)/{chainId}/{seller}/{sku}
            for (int i = 0; i + 3 < segments.Length; i++)
            {
                bool isInventory = string.Equals(segments[i], "inventory", StringComparison.OrdinalIgnoreCase);
                bool isAvailability = string.Equals(segments[i], "availability", StringComparison.OrdinalIgnoreCase);
                if (!isInventory && !isAvailability)
                {
                    continue;
                }

                bool chainOk = long.TryParse(segments[i + 1], out var chain);
                bool sellerOk = segments[i + 2].StartsWith("0x", StringComparison.OrdinalIgnoreCase) && segments[i + 2].Length == 42;
                if (chainOk && sellerOk)
                {
                    return (segments[i + 2].ToLowerInvariant(), chain);
                }
            }
        }
        catch { }
        return (null, 0);
    }
}
