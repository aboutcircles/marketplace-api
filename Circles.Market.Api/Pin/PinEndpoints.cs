using System.Net;
using Circles.Profiles.Interfaces;

namespace Circles.Market.Api.Pin;

public static class PinEndpoints
{
    private static readonly int MaxUploadBytes = MarketConstants.Limits.MaxUploadBytes; // 8 MiB cap

    public static IEndpointRouteBuilder MapPinApi(this IEndpointRouteBuilder app)
    {
        app.MapPost(MarketConstants.Routes.Pin, Pin)
            .WithSummary("Pin user-authored JSON-LD to IPFS and return its CID")
            .WithDescription(
                "Accepts raw JSON-LD; verifies payload shape against allowed user-generated models only, stores via IPFS /add + /pin/add, returns { cid } JSON.")
            .Accepts<string>(MarketConstants.ContentTypes.JsonLdUtf8, MarketConstants.ContentTypes.Json)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status413PayloadTooLarge);

        app.MapPost(MarketConstants.Routes.PinMedia, PinMedia)
            .WithSummary("Pin binary media (e.g. images) to IPFS and return its CID")
            .WithDescription(
                "Accepts raw binary data (image/*, application/octet-stream); applies the same 8 MiB limit, stores via IPFS /add + /pin/add, returns { cid } JSON.")
            .Accepts<IFormFile>(MarketConstants.ContentTypes.AnyImage, MarketConstants.ContentTypes.OctetStream)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status413PayloadTooLarge);

        return app;
    }

    private static async Task<IResult> Pin(
        HttpRequest req,
        IIpfsStore ipfs,
        IJsonLdShapeVerifier verifier,
        CancellationToken ct
    )
    {
        try
        {
            await using var body = req.Body;
            using var ms = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int read;
            long total = 0;
            while ((read = await body.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                total += read;
                if (total > MaxUploadBytes)
                {
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }

                ms.Write(buffer, 0, read);
            }

            var bytes = ms.ToArray();

            if (!verifier.CanPin(bytes, out var reason))
            {
                return Results.Problem(
                    title: "Unsupported JSON-LD shape",
                    detail: reason,
                    statusCode: (int)HttpStatusCode.BadRequest
                );
            }

            var cid = await ipfs.AddBytesAsync(bytes, pin: true, ct);
            return Results.Json(new { cid });
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(
                title: "Invalid request",
                detail: ex.Message,
                statusCode: (int)HttpStatusCode.BadRequest
            );
        }
        catch (PayloadTooLargeException)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }
    }

    private static async Task<IResult> PinMedia(
        HttpRequest req,
        IIpfsStore ipfs,
        CancellationToken ct
    )
    {
        try
        {
            await using var body = req.Body;
            using var ms = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int read;
            long total = 0;
            while ((read = await body.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                total += read;
                if (total > MaxUploadBytes)
                {
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }

                ms.Write(buffer, 0, read);
            }

            var bytes = ms.ToArray();
            if (bytes.Length == 0)
            {
                return Results.Problem(
                    title: "Invalid request",
                    detail: "Empty media payload",
                    statusCode: (int)HttpStatusCode.BadRequest
                );
            }

            var cid = await ipfs.AddBytesAsync(bytes, pin: true, ct);
            return Results.Json(new { cid });
        }
        catch (PayloadTooLargeException)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(
                title: "Invalid request",
                detail: ex.Message,
                statusCode: (int)HttpStatusCode.BadRequest
            );
        }
    }
}
