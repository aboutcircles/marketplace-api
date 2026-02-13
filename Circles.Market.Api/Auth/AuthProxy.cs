namespace Circles.Market.Api.Auth;

/// <summary>
/// Proxies /api/auth/* requests to the centralized auth-service (DigitalOcean).
/// The auth-service handles SIWE challenge/verify and issues RS256 JWTs.
/// This keeps the frontend URL unchanged while delegating auth entirely to the auth-service.
/// </summary>
public static class AuthProxy
{
    public static IEndpointRouteBuilder MapAuthProxy(this IEndpointRouteBuilder app, string basePath = "/api/auth")
    {
        string authServiceUrl = Environment.GetEnvironmentVariable("AUTH_SERVICE_URL")
            ?? throw new InvalidOperationException("AUTH_SERVICE_URL is required for auth proxy");

        string upstream = authServiceUrl.TrimEnd('/');

        var group = app.MapGroup(basePath);

        group.MapPost("/challenge", async (HttpContext ctx, IHttpClientFactory httpFactory) =>
            await ProxyPost(ctx, httpFactory, $"{upstream}/challenge"))
            .WithSummary("Proxy auth challenge to auth-service");

        group.MapPost("/verify", async (HttpContext ctx, IHttpClientFactory httpFactory) =>
            await ProxyPost(ctx, httpFactory, $"{upstream}/verify"))
            .WithSummary("Proxy auth verify to auth-service");

        return app;
    }

    private static async Task<IResult> ProxyPost(HttpContext ctx, IHttpClientFactory httpFactory, string targetUrl)
    {
        var log = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("AuthProxy");

        try
        {
            using var client = httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);

            // Forward the request body as-is
            using var content = new StreamContent(ctx.Request.Body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                ctx.Request.ContentType ?? "application/json");

            // Forward relevant headers
            if (ctx.Request.Headers.TryGetValue("X-Forwarded-For", out var xff))
                client.DefaultRequestHeaders.TryAddWithoutValidation("X-Forwarded-For", xff.ToString());
            if (ctx.Request.Headers.TryGetValue("Origin", out var origin))
                client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", origin.ToString());

            var response = await client.PostAsync(targetUrl, content, ctx.RequestAborted);

            // Forward response
            ctx.Response.StatusCode = (int)response.StatusCode;
            ctx.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";

            await response.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
            return Results.Empty;
        }
        catch (TaskCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
        {
            log.LogError("Auth-service proxy timeout: {Url}", targetUrl);
            return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
        }
        catch (HttpRequestException ex)
        {
            log.LogError(ex, "Auth-service proxy error: {Url}", targetUrl);
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
    }
}
