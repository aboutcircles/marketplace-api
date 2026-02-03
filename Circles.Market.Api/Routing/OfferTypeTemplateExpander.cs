using System.Text.RegularExpressions;

namespace Circles.Market.Api.Routing;

public static class OfferTypeTemplateExpander
{
    private static readonly Regex Placeholder = new(@"\{([A-Za-z0-9_]+)\}", RegexOptions.Compiled);

    public static bool TryExpand(
        string template,
        long chainId,
        string seller,
        string sku,
        out string? expanded,
        out string? error)
    {
        expanded = null;
        error = null;

        if (string.IsNullOrWhiteSpace(template))
        {
            error = "Template is empty";
            return false;
        }

        string sellerNorm = seller.Trim().ToLowerInvariant();
        string skuNorm = sku.Trim().ToLowerInvariant();

        // Encode for safety when inserted into URL path segments
        string sellerEsc = Uri.EscapeDataString(sellerNorm);
        string skuEsc = Uri.EscapeDataString(skuNorm);
        string chainStr = chainId.ToString();

        int marketApiPort = GetPortEnv("MARKET_API_PORT", 5084);
        int odooPort = GetPortEnv("MARKET_ODOO_ADAPTER_PORT", 5678);
        int codedispPort = GetPortEnv("MARKET_CODE_DISPENSER_PORT", 5680);

        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["seller"] = sellerEsc,
            ["sku"] = skuEsc,
            ["chain_id"] = chainStr,
            ["MARKET_API_PORT"] = marketApiPort.ToString(),
            ["MARKET_ODOO_ADAPTER_PORT"] = odooPort.ToString(),
            ["MARKET_CODE_DISPENSER_PORT"] = codedispPort.ToString(),
        };

        string? localError = null;

        string result = Placeholder.Replace(template, m =>
        {
            string key = m.Groups[1].Value;
            if (!vars.TryGetValue(key, out var value))
            {
                localError = $"Unknown template variable: {key}";
                return m.Value;
            }
            return value;
        });

        if (localError != null)
        {
            expanded = null;
            error = localError;
            return false;
        }

        expanded = result;
        return true;
    }

    private static int GetPortEnv(string name, int defaultPort)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out var p) && p > 0 && p <= 65535)
        {
            return p;
        }
        return defaultPort;
    }
}
