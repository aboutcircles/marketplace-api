using Circles.Profiles.Models.Core;
using Circles.Profiles.Sdk.Utils;
using Microsoft.AspNetCore.Http;

namespace Circles.Market.Auth.Siwe;

internal static class SiweAuthConfig
{
    public static (string domain, Uri baseUri) ResolveBaseUriAndValidate(HttpContext ctx, SiweAuthOptions options)
    {
        string? allowed = Environment.GetEnvironmentVariable(options.AllowedDomainsEnv);
        if (string.IsNullOrWhiteSpace(allowed))
            throw new ArgumentException($"{options.AllowedDomainsEnv} must be configured");

        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in allowed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            hosts.Add(h);
        }
        bool allowAny = hosts.Contains("*");

        string? baseUrl = Environment.GetEnvironmentVariable(options.PublicBaseUrlEnv ?? string.Empty);
        if (string.IsNullOrWhiteSpace(baseUrl) && !string.IsNullOrWhiteSpace(options.ExternalBaseUrlEnv))
        {
            baseUrl = Environment.GetEnvironmentVariable(options.ExternalBaseUrlEnv);
        }

        Uri baseUri;
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out baseUri!))
                throw new ArgumentException("PUBLIC/EXTERNAL base URL must be an absolute URI");
        }
        else
        {
            if (options.RequirePublicBaseUrl)
                throw new ArgumentException("PUBLIC base URL must be configured");

            var scheme = ctx.Request.Scheme;
            var host = ctx.Request.Host;
            if (string.IsNullOrEmpty(host.Host))
                throw new ArgumentException("Request host is missing");
            var builder = new UriBuilder(scheme, host.Host, host.Port ?? (string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80));
            baseUri = builder.Uri;
        }

        string domain = baseUri.Host;
        if (!allowAny && !hosts.Contains(domain))
            throw new ArgumentException("Requested host is not allowlisted");

        return (domain, baseUri);
    }

    public static HashSet<string> LoadAllowlist(SiweAuthOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.AllowlistEnv))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var raw = Environment.GetEnvironmentVariable(options.AllowlistEnv!) ?? string.Empty;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var addr = AddressUtils.NormalizeToLowercase(part.Trim());
            if (!string.IsNullOrWhiteSpace(addr)) set.Add(addr);
        }
        return set;
    }
}
