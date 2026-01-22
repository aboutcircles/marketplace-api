using System.Net;
using System.Net.Sockets;

namespace Circles.Market.Api.Outbound;

public static class OutboundGuards
{
    public const string HopHeader = "X-Circles-Proxy-Hop";

    public static bool IsHttpOrHttps(Uri uri)
    {
        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
    }

    public static async Task<bool> IsPrivateOrLocalTargetAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(uri.Host, ct);
            return addresses.Any(IsPrivateOrLocal);
        }
        catch
        {
            // If we can't resolve, better safe than sorry? 
            // Or maybe it's just an invalid host. 
            // For SSRF protection, we block if we can't be sure it's public.
            return true; 
        }
    }

    private static bool IsPrivateOrLocal(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.None) || 
            ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6None)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] bytes = ip.GetAddressBytes();
            // 10.0.0.0/8
            if (bytes[0] == 10) return true;
            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            // 169.254.0.0/16 (Link-local)
            if (bytes[0] == 169 && bytes[1] == 254) return true;
            // 224.0.0.0/4 (Multicast)
            if ((bytes[0] & 0xf0) == 0xe0) return true;
            // 0.0.0.0/8 (This network)
            if (bytes[0] == 0) return true;
        }
        else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal) return true;
            if (ip.IsIPv6SiteLocal) return true;
            if (ip.IsIPv6Multicast) return true;
            // Unique Local Address (fc00::/7)
            byte[] bytes = ip.GetAddressBytes();
            if ((bytes[0] & 0xfe) == 0xfc) return true;
        }

        return false;
    }

    public static void AddHopHeader(HttpRequestMessage req)
    {
        req.Headers.TryAddWithoutValidation(HopHeader, "1");
    }

    public static async Task<byte[]> ReadWithLimitAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        var len = content.Headers.ContentLength;
        if (len.HasValue && len.Value > maxBytes)
        {
            throw new HttpRequestException($"Response size exceeded limit of {maxBytes} bytes (via Content-Length).");
        }

        var stream = await content.ReadAsStreamAsync(ct);
        int initialCapacity = len.HasValue && len.Value <= maxBytes ? (int)len.Value : Math.Min(8192, maxBytes);
        using var ms = new System.IO.MemoryStream(initialCapacity);
        
        var buffer = new byte[Math.Min(8192, maxBytes)];
        int totalRead = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, 0, Math.Min(buffer.Length, maxBytes - totalRead), ct)) > 0)
        {
            ms.Write(buffer, 0, read);
            totalRead += read;
            if (totalRead >= maxBytes)
            {
                // Check if there is more without using a shared buffer
                var probe = new byte[1];
                if (await stream.ReadAsync(probe, 0, 1, ct) > 0)
                {
                    throw new HttpRequestException($"Response size exceeded limit of {maxBytes} bytes.");
                }
                break;
            }
        }
        return ms.ToArray();
    }

    public static async Task<HttpResponseMessage> SendWithRedirectsAsync(
        HttpClient client,
        HttpRequestMessage initialRequest,
        int maxRedirects,
        Func<Uri, HttpRequestMessage> rebuildRequest,
        CancellationToken ct)
    {
        var currentRequest = initialRequest;
        var effectiveMethod = initialRequest.Method;
        int redirectsFollowed = 0;

        while (true)
        {
            var baseUri = currentRequest.RequestUri;
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(currentRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            finally
            {
                // Request no longer needed after SendAsync
                currentRequest.Dispose();
            }

            bool isRedirect =
                response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or
                HttpStatusCode.TemporaryRedirect or (HttpStatusCode)308;

            if (!isRedirect)
            {
                return response;
            }

            if (redirectsFollowed >= maxRedirects)
            {
                response.Dispose();
                throw new HttpRequestException("Too many redirects");
            }

            var location = response.Headers.Location;
            if (location == null)
            {
                // Can't follow without Location; return the response to the caller.
                return response;
            }

            if (!location.IsAbsoluteUri)
            {
                if (baseUri == null)
                {
                    response.Dispose();
                    throw new InvalidOperationException("Missing base URI to resolve relative redirect");
                }
                location = new Uri(baseUri, location);
            }

            if (!IsHttpOrHttps(location))
            {
                response.Dispose();
                throw new HttpRequestException($"Invalid redirect scheme: {location.Scheme}");
            }

            if (await IsPrivateOrLocalTargetAsync(location, ct))
            {
                response.Dispose();
                throw new HttpRequestException("Blocked private redirect target", null, HttpStatusCode.BadGateway);
            }

            var status = response.StatusCode;
            var priorMethod = effectiveMethod;

            response.Dispose();

            redirectsFollowed++;

            // Emulate common HttpClient redirect semantics:
            bool switchToGet =
                status == HttpStatusCode.SeeOther ||
                ((status == HttpStatusCode.MovedPermanently || status == HttpStatusCode.Found) && priorMethod == HttpMethod.Post);

            if (switchToGet)
            {
                effectiveMethod = HttpMethod.Get;
            }

            var nextRequest = rebuildRequest(location);
            nextRequest.Method = effectiveMethod;
            if (effectiveMethod == HttpMethod.Get)
            {
                nextRequest.Content = null;
            }

            currentRequest = nextRequest;
        }
    }

    // TODO: To fully protect against DNS rebinding, we should implement a ConnectCallback 
    // in SocketsHttpHandler that re-validates the chosen IP address at connect time.

    public static int GetAvailabilityTimeoutMs() => GetEnvInt("OUTBOUND_AVAILABILITY_TIMEOUT_MS", 800);
    public static int GetInventoryTimeoutMs() => GetEnvInt("OUTBOUND_INVENTORY_TIMEOUT_MS", 800);
    public static int GetFulfillmentTimeoutMs() => GetEnvInt("OUTBOUND_FULFILLMENT_TIMEOUT_MS", 1500);
    public static int GetMaxResponseBytes() => GetEnvInt("OUTBOUND_MAX_RESPONSE_BYTES", 65536);
    public static int GetMaxRedirects() => GetEnvInt("OUTBOUND_MAX_REDIRECTS", 3);

    private static int GetEnvInt(string name, int defaultValue)
    {
        var val = Environment.GetEnvironmentVariable(name);
        return int.TryParse(val, out var result) ? result : defaultValue;
    }
}
