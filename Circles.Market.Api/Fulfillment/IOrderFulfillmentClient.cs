using System.Text.Json;
using Circles.Market.Api.Outbound;

namespace Circles.Market.Api.Fulfillment;

public interface IOrderFulfillmentClient
{
    Task<JsonElement> FulfillAsync(
        string endpoint,
        string orderId,
        string paymentReference,
        Cart.OrderSnapshot order,
        string trigger,
        CancellationToken ct = default);
}

public sealed class HttpOrderFulfillmentClient : IOrderFulfillmentClient
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<HttpOrderFulfillmentClient> _log;
    private readonly Circles.Market.Api.Auth.IOutboundServiceAuthProvider _authProvider;

    public HttpOrderFulfillmentClient(IHttpClientFactory http, ILogger<HttpOrderFulfillmentClient> log, Circles.Market.Api.Auth.IOutboundServiceAuthProvider authProvider)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _authProvider = authProvider ?? throw new ArgumentNullException(nameof(authProvider));
    }

    public async Task<JsonElement> FulfillAsync(string endpoint, string orderId, string paymentReference, Cart.OrderSnapshot order, string trigger, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) throw new ArgumentException("Endpoint is required", nameof(endpoint));

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || !OutboundGuards.IsHttpOrHttps(uri))
        {
            throw new ArgumentException("Invalid or non-HTTP/HTTPS fulfillment endpoint", nameof(endpoint));
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(OutboundGuards.GetFulfillmentTimeoutMs()));

        var (seller, chain) = TryParseFulfillmentPath(uri);

        (string headerName, string apiKey)? header = null;
        bool hasParsedTarget = !string.IsNullOrWhiteSpace(seller) && chain > 0;
        if (hasParsedTarget)
        {
            header = await _authProvider.TryGetHeaderAsync(
                uri,
                serviceKind: "fulfillment",
                sellerAddress: seller,
                chainId: chain,
                ct: timeoutCts.Token);
        }

        if (header == null)
        {
            _log.LogWarning("FulfillAsync: No outbound auth header found for target {Uri} (Seller={Seller}, Chain={Chain}). Falling back to public request.",
                uri, seller ?? "_", chain);
            if (await OutboundGuards.IsPrivateOrLocalTargetAsync(uri, timeoutCts.Token))
            {
                _log.LogError("FulfillAsync: Blocked private fulfillment address {Uri} as no auth header was provided.", uri);
                throw new HttpRequestException("Blocked private fulfillment address", null, System.Net.HttpStatusCode.BadGateway);
            }
        }
        else
        {
            _log.LogInformation("FulfillAsync: Using trusted auth header for {Uri}", uri);
        }

        using var client = _http.CreateClient(header != null ? "fulfillment_trusted" : "fulfillment_public");

        var items = order.OrderedItem.Select(i => new
        {
            sku = i.OrderedItem.Sku,
            quantity = i.OrderQuantity
        }).ToArray();

        var payload = new Dictionary<string, object?>
        {
            ["orderId"] = orderId,
            ["paymentReference"] = paymentReference,
            ["buyer"] = ExtractBuyerAddress(order.Customer?.Id),
            ["customer"] = BuildCustomerPayload(order.Customer),
            ["shippingAddress"] = BuildAddressPayload(order.ShippingAddress),
            ["billingAddress"] = BuildAddressPayload(order.BillingAddress),
            ["contactPoint"] = BuildContactPointPayload(order.SellerContact),
            ["items"] = items,
            ["trigger"] = trigger,
            ["amountWei"] = order.PaidAmountWei,
            ["paymentTimestamp"] = order.PaidAt,
        };

        var jsonContent = System.Text.Json.JsonSerializer.Serialize(payload, Circles.Profiles.Models.JsonSerializerOptions.JsonLd);
        Func<Uri, HttpRequestMessage> createReq = u => new HttpRequestMessage(HttpMethod.Post, u)
        {
            Content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json"),
            Headers = { { OutboundGuards.HopHeader, "1" } }
        };

        HttpResponseMessage resp;
        if (header != null)
        {
            using var req = createReq(uri);
            req.Headers.TryAddWithoutValidation(header.Value.headerName, header.Value.apiKey);
            resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
        }
        else
        {
            using var initialReq = createReq(uri);
            resp = await OutboundGuards.SendWithRedirectsAsync(client, initialReq, OutboundGuards.GetMaxRedirects(), createReq, timeoutCts.Token);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(timeoutCts.Token);
                _log.LogWarning("Fulfillment endpoint {Endpoint} returned {Status}: {Body}", endpoint, (int)resp.StatusCode, body);
                resp.EnsureSuccessStatusCode();
            }

            var bytes = await OutboundGuards.ReadWithLimitAsync(resp.Content, OutboundGuards.GetMaxResponseBytes(), timeoutCts.Token);
            using var doc = JsonDocument.Parse(bytes);
            return doc.RootElement.Clone();
        }
    }

    private static object? BuildCustomerPayload(Cart.SchemaOrgPersonId? customer)
    {
        if (customer == null)
        {
            return null;
        }

        string? givenName = string.IsNullOrWhiteSpace(customer.GivenName) ? null : customer.GivenName.Trim();
        string? familyName = string.IsNullOrWhiteSpace(customer.FamilyName) ? null : customer.FamilyName.Trim();

        string? fullName = (givenName, familyName) switch
        {
            (not null, not null) => $"{givenName} {familyName}",
            (not null, null) => givenName,
            (null, not null) => familyName,
            _ => null
        };

        return new
        {
            name = fullName,
            givenName,
            familyName
        };
    }

    private static object? BuildAddressPayload(Cart.PostalAddress? address)
    {
        if (address == null)
        {
            return null;
        }

        return new
        {
            streetAddress = address.StreetAddress,
            addressLocality = address.AddressLocality,
            postalCode = address.PostalCode,
            addressCountry = address.AddressCountry
        };
    }

    private static object? BuildContactPointPayload(Cart.ContactPoint? contactPoint)
    {
        if (contactPoint == null)
        {
            return null;
        }

        return new
        {
            email = contactPoint.Email,
            telephone = contactPoint.Telephone
        };
    }

    private static (string? seller, long chainId) TryParseFulfillmentPath(Uri uri)
    {
        try
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            // expect: fulfill/{chainId}/{seller}
            for (int i = 0; i + 2 < segments.Length; i++)
            {
                if (string.Equals(segments[i], "fulfill", StringComparison.OrdinalIgnoreCase))
                {
                    if (long.TryParse(segments[i + 1], out var chain) && segments[i + 2].StartsWith("0x") && segments[i + 2].Length == 42)
                    {
                        return (segments[i + 2].ToLowerInvariant(), chain);
                    }
                }
            }
        }
        catch { }
        return (null, 0);
    }

    private static string? ExtractBuyerAddress(string? customerId)
    {
        if (string.IsNullOrWhiteSpace(customerId))
        {
            return null;
        }

        var trimmed = customerId.Trim();

        // Common shape in order snapshots: eip155:{chainId}:{address}
        var parts = trimmed.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 3 && string.Equals(parts[0], "eip155", StringComparison.OrdinalIgnoreCase))
        {
            var addr = parts[2];
            return addr.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && addr.Length == 42
                ? addr.ToLowerInvariant()
                : null;
        }

        // Also accept direct EVM address format.
        return trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && trimmed.Length == 42
            ? trimmed.ToLowerInvariant()
            : null;
    }
}
