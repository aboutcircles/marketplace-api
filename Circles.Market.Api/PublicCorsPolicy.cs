using Microsoft.AspNetCore.Cors.Infrastructure;

namespace Circles.Market.Api;

/// <summary>
/// CORS policy for the public app: any origin may call it, none may send credentials.
/// Buyers authenticate with an <c>Authorization: Bearer</c> header, which a browser
/// never attaches by itself, and this app sets no cookies. Allowing credentials would
/// only let any website make cookie-carrying requests that no client needs.
/// </summary>
public static class PublicCorsPolicy
{
    public const string Name = "AllowAll";

    public static void Configure(CorsPolicyBuilder policy) => policy
        .AllowAnyOrigin()
        .AllowAnyMethod()
        .AllowAnyHeader()
        .WithExposedHeaders("X-Next-Cursor", "Link");
}
