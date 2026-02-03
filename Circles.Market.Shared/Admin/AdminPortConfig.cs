using System.Linq;
using Microsoft.AspNetCore.Builder;

namespace Circles.Market.Shared.Admin;

public static class AdminPortConfig
{
    public static int GetAdminPort(string envName, int defaultPort)
    {
        string? raw = Environment.GetEnvironmentVariable(envName);
        if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out var port) && port > 0 && port <= 65535)
        {
            return port;
        }

        return defaultPort;
    }

    public static void AddAdminUrl(WebApplication app, int adminPort)
    {
        string url = $"http://0.0.0.0:{adminPort}";
        if (!app.Urls.Any(u => string.Equals(u, url, StringComparison.OrdinalIgnoreCase)))
        {
            app.Urls.Add(url);
        }
    }
}
