using System.Text.Json;
using Circles.Profiles.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Circles.Market.Auth.Siwe;

public static class SiweAuthEndpoints
{
    public static IEndpointRouteBuilder MapSiweAuthApi(
        this IEndpointRouteBuilder app,
        string basePath,
        string defaultStatement,
        string contentType)
    {
        var group = app.MapGroup(basePath);

        group.MapPost("/challenge", async (HttpContext ctx, SiweAuthService service) =>
            await CreateChallenge(ctx, service, defaultStatement, contentType))
            .WithSummary("Create an auth challenge (SIWE-like message)")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest);

        group.MapPost("/verify", async (HttpContext ctx, VerifyRequest req, SiweAuthService service, CancellationToken ct) =>
            await Verify(ctx, req, service, ct, contentType))
            .WithSummary("Verify challenge signature and issue JWT")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized);

        return app;
    }

    private static async Task<IResult> CreateChallenge(HttpContext ctx, SiweAuthService service, string defaultStatement, string contentType)
    {
        ctx.Response.ContentType = contentType;
        try
        {
            var req = await JsonSerializer.DeserializeAsync<ChallengeRequest>(
                ctx.Request.Body,
                Circles.Profiles.Models.JsonSerializerOptions.JsonLd,
                ctx.RequestAborted) ?? new ChallengeRequest();

            var res = await service.CreateChallengeAsync(ctx, req, defaultStatement);
            return Results.Json(res, Circles.Profiles.Models.JsonSerializerOptions.JsonLd, contentType: contentType);
        }
        catch (ArgumentException ex)
        {
            return Results.Json(new { error = ex.Message }, contentType: contentType, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> Verify(HttpContext ctx, VerifyRequest req, SiweAuthService service, CancellationToken ct, string contentType)
    {
        ctx.Response.ContentType = contentType;
        var log = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("SiweVerify");

        try
        {
            var res = await service.VerifyAsync(req, ct);
            return Results.Json(res, Circles.Profiles.Models.JsonSerializerOptions.JsonLd, contentType: contentType);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }
        catch (InvalidOperationException ex)
        {
            log.LogWarning(ex, "Allowlist configuration error");
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }
        catch (HttpRequestException ex)
        {
            log.LogError(ex, "Verification transport error (RPC)");
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
        catch (IOException ex)
        {
            log.LogError(ex, "Verification I/O error");
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            log.LogError(ex, "Verification timeout");
            return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Verification internal error");
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }
    }
}
