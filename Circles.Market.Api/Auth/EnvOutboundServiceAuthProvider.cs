using Microsoft.Extensions.Logging;

namespace Circles.Market.Api.Auth;

public sealed class EnvOutboundServiceAuthProvider : IOutboundServiceAuthProvider
{
    private readonly ILogger<EnvOutboundServiceAuthProvider> _log;
    private readonly string _headerName;

    private readonly string _odooOrigin;
    private readonly string? _odooToken;

    private readonly string _codedispOrigin;
    private readonly string? _codedispToken;

    private readonly string _unlockOrigin;
    private readonly string? _unlockToken;

    private readonly string _woocommerceOrigin;
    private readonly string? _woocommerceToken;

    public EnvOutboundServiceAuthProvider(ILogger<EnvOutboundServiceAuthProvider> log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));

        _headerName = Environment.GetEnvironmentVariable("MARKET_OUTBOUND_HEADER_NAME");
        if (string.IsNullOrWhiteSpace(_headerName))
        {
            _headerName = "X-Circles-Service-Key";
        }

        int odooPort = GetPortEnv("MARKET_ODOO_ADAPTER_PORT", 5678);
        int codedispPort = GetPortEnv("MARKET_CODE_DISPENSER_PORT", 5680);
        int unlockPort = GetPortEnv("MARKET_UNLOCK_ADAPTER_PORT", 5682);
        int woocommercePort = GetPortEnv("MARKET_WOOCOMMERCE_ADAPTER_PORT", 5679);

        var odooOriginOverride = Environment.GetEnvironmentVariable("MARKET_ODOO_ADAPTER_ORIGIN");
        _odooOrigin = !string.IsNullOrWhiteSpace(odooOriginOverride)
            ? NormalizeOriginString(odooOriginOverride)
            : $"http://market-adapter-odoo:{odooPort}";

        var codedispOriginOverride = Environment.GetEnvironmentVariable("MARKET_CODE_DISPENSER_ORIGIN");
        _codedispOrigin = !string.IsNullOrWhiteSpace(codedispOriginOverride)
            ? NormalizeOriginString(codedispOriginOverride)
            : $"http://market-adapter-codedispenser:{codedispPort}";

        var unlockOriginOverride = Environment.GetEnvironmentVariable("MARKET_UNLOCK_ADAPTER_ORIGIN");
        _unlockOrigin = !string.IsNullOrWhiteSpace(unlockOriginOverride)
            ? NormalizeOriginString(unlockOriginOverride)
            : $"http://market-adapter-unlock:{unlockPort}";

        var woocommerceOriginOverride = Environment.GetEnvironmentVariable("MARKET_WOOCOMMERCE_ADAPTER_ORIGIN");
        _woocommerceOrigin = !string.IsNullOrWhiteSpace(woocommerceOriginOverride)
            ? NormalizeOriginString(woocommerceOriginOverride)
            : $"http://market-adapter-woocommerce:{woocommercePort}";

        var shared = ReadToken("CIRCLES_SERVICE_KEY");

        _odooToken = ReadToken("MARKET_ODOO_ADAPTER_TOKEN") ?? shared;
        _codedispToken = ReadToken("MARKET_CODE_DISPENSER_TOKEN") ?? shared;
        _unlockToken = ReadToken("MARKET_UNLOCK_ADAPTER_TOKEN") ?? shared;
        _woocommerceToken = ReadToken("MARKET_WOOCOMMERCE_ADAPTER_TOKEN") ?? shared;

        _log.LogInformation("EnvOutboundServiceAuthProvider configured: header={HeaderName} odooOrigin={OdooOrigin} codedispOrigin={CodeDispOrigin} unlockOrigin={UnlockOrigin} woocommerceOrigin={WooCommerceOrigin}",
            _headerName, _odooOrigin, _codedispOrigin, _unlockOrigin, _woocommerceOrigin);
    }

    public Task<(string headerName, string apiKey)?> TryGetHeaderAsync(
        Uri endpoint,
        string serviceKind,
        string? sellerAddress,
        long chainId,
        CancellationToken ct = default)
    {
        if (endpoint is null)
        {
            throw new ArgumentNullException(nameof(endpoint));
        }
        if (string.IsNullOrWhiteSpace(serviceKind))
        {
            throw new ArgumentException("serviceKind is required", nameof(serviceKind));
        }

        var origin = NormalizeOrigin(endpoint);

        if (string.Equals(origin, _odooOrigin, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<(string headerName, string apiKey)?>(MakeHeaderOrNull(_odooToken, "odoo", origin, serviceKind));
        }

        if (string.Equals(origin, _codedispOrigin, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<(string headerName, string apiKey)?>(MakeHeaderOrNull(_codedispToken, "codedispenser", origin, serviceKind));
        }

        if (string.Equals(origin, _unlockOrigin, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<(string headerName, string apiKey)?>(MakeHeaderOrNull(_unlockToken, "unlock", origin, serviceKind));
        }

        if (string.Equals(origin, _woocommerceOrigin, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<(string headerName, string apiKey)?>(MakeHeaderOrNull(_woocommerceToken, "woocommerce", origin, serviceKind));
        }

        return Task.FromResult<(string headerName, string apiKey)?>(null);
    }

    private (string headerName, string apiKey)? MakeHeaderOrNull(string? token, string offerType, string origin, string serviceKind)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            _log.LogWarning("No outbound token configured for offerType={OfferType} origin={Origin} kind={Kind}", offerType, origin, serviceKind);
            return null;
        }

        return (_headerName, token);
    }

    private static string? ReadToken(string name)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        return raw.Trim();
    }

    private static int GetPortEnv(string name, int defaultPort)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out var p) && p > 0 && p <= 65535)
        {
            return p;
        }
        return defaultPort;
    }

    private static string NormalizeOrigin(Uri endpoint)
    {
        var scheme = endpoint.Scheme.ToLowerInvariant();
        var host = endpoint.Host.ToLowerInvariant();
        int port = endpoint.Port;

        bool includePort = !(scheme == "http" && port == 80) && !(scheme == "https" && port == 443);
        return includePort ? $"{scheme}://{host}:{port}" : $"{scheme}://{host}";
    }

    private static string NormalizeOriginString(string origin)
    {
        if (!Uri.TryCreate(origin.Trim(), UriKind.Absolute, out var uri))
        {
            throw new ArgumentException($"Invalid origin URI: {origin}", nameof(origin));
        }
        return NormalizeOrigin(uri);
    }
}
