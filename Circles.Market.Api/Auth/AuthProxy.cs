using System.Text.Json;

namespace Circles.Market.Api.Auth;

/// <summary>
/// Proxies /auth/* requests to the centralized auth-service.
/// The auth-service handles SIWE challenge/verify and issues RS256 JWTs.
/// This keeps the frontend URL unchanged while delegating auth entirely to the auth-service.
///
/// The proxy injects the configured JWT audience(s) into /challenge requests so the
/// auth-service issues tokens with the correct aud claim. Frontend-supplied audience values
/// are overridden — each marketplace-api app dictates which audience(s) its tokens carry.
/// </summary>
public static class AuthProxy
{
    /// <summary>
    /// Maps /challenge and /verify endpoints under <paramref name="basePath"/>, proxied to the
    /// auth-service. Pass <paramref name="audiences"/> with the audience(s) this app expects in
    /// minted JWTs: <c>["market-api"]</c> for the public app, <c>["market-admin-api"]</c> for
    /// the admin app. Admin tokens are deliberately single-audience and never usable on the
    /// public app — admins sign in separately for each surface.
    /// </summary>
    public static IEndpointRouteBuilder MapAuthProxy(
        this IEndpointRouteBuilder app, string basePath, string[] audiences)
    {
        if (audiences.Length == 0)
            throw new ArgumentException("audiences must not be empty", nameof(audiences));

        string authServiceUrl = Environment.GetEnvironmentVariable("AUTH_SERVICE_URL")
            ?? throw new InvalidOperationException("AUTH_SERVICE_URL is required for auth proxy");

        string upstream = authServiceUrl.TrimEnd('/');

        var group = app.MapGroup(basePath);

        group.MapPost("/challenge", async (HttpContext ctx, IHttpClientFactory httpFactory) =>
            await ProxyChallenge(ctx, httpFactory, $"{upstream}/challenge", audiences))
            .WithSummary("Proxy auth challenge to auth-service");

        group.MapPost("/verify", async (HttpContext ctx, IHttpClientFactory httpFactory) =>
            await ProxyPost(ctx, httpFactory, $"{upstream}/verify"))
            .WithSummary("Proxy auth verify to auth-service");

        return app;
    }

    /// <summary>
    /// Proxies the /challenge request and injects "audience" into the JSON body
    /// so the auth-service issues tokens with the correct aud claim. Always serializes as array;
    /// the auth-service zod schema accepts string | string[].
    /// </summary>
    private static async Task<IResult> ProxyChallenge(
        HttpContext ctx, IHttpClientFactory httpFactory, string targetUrl, string[] audiences)
    {
        var log = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("AuthProxy");

        try
        {
            using var client = httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);

            // Read the original body, inject audience
            var bodyBytes = await ReadBodyAsync(ctx.Request.Body, ctx.RequestAborted);
            var doc = JsonSerializer.Deserialize<JsonElement>(bodyBytes);
            var merged = new Dictionary<string, JsonElement>();
            if (doc.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in doc.EnumerateObject())
                    merged[prop.Name] = prop.Value;
            }
            // Inject audience(s) (override if frontend sent a different value)
            merged["audience"] = JsonSerializer.SerializeToElement(audiences);

            var newBody = JsonSerializer.SerializeToUtf8Bytes(merged);
            using var content = new ByteArrayContent(newBody);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

            ForwardHeaders(ctx, client);

            var response = await client.PostAsync(targetUrl, content, ctx.RequestAborted);

            ctx.Response.StatusCode = (int)response.StatusCode;
            ctx.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
            await response.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
            return Results.Empty;
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "Auth proxy: malformed challenge request body");
            return Results.BadRequest(new { error = "Invalid JSON in challenge request" });
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

    private static async Task<IResult> ProxyPost(HttpContext ctx, IHttpClientFactory httpFactory, string targetUrl)
    {
        var log = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("AuthProxy");

        try
        {
            using var client = httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);

            using var content = new StreamContent(ctx.Request.Body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                ctx.Request.ContentType ?? "application/json");

            ForwardHeaders(ctx, client);

            var response = await client.PostAsync(targetUrl, content, ctx.RequestAborted);

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

    private static void ForwardHeaders(HttpContext ctx, HttpClient client)
    {
        if (ctx.Request.Headers.TryGetValue("X-Forwarded-For", out var xff))
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-Forwarded-For", xff.ToString());
        if (ctx.Request.Headers.TryGetValue("Origin", out var origin))
            client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", origin.ToString());
    }

    private static async Task<byte[]> ReadBodyAsync(Stream body, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await body.CopyToAsync(ms, ct);
        return ms.ToArray();
    }
}
