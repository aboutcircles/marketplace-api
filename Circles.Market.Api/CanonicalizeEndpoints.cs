using System.Net;
using System.Text;
using System.Text.Json;
using Circles.Profiles.Models.Core;
using Circles.Profiles.Sdk.Utils;

namespace Circles.Market.Api;

public static class CanonicalizeEndpoints
{
    private static readonly int MaxUploadBytes = MarketConstants.Limits.MaxUploadBytes; // align with pin endpoint

    public static IEndpointRouteBuilder MapCanonicalizeApi(this IEndpointRouteBuilder app)
    {
        app.MapPost(MarketConstants.Routes.Canonicalize, Canonicalize)
            .WithSummary("Return the RFC8785-like canonical JSON representation used for signing")
            .WithDescription("Accepts a JSON-LD CustomDataLink and returns its canonical JSON (without the signature field) using the SDK's CanonicalJson.")
            .Accepts<string>(MarketConstants.ContentTypes.JsonLdUtf8, MarketConstants.ContentTypes.Json)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status413PayloadTooLarge);
        return app;
    }

    private static async Task<IResult> Canonicalize(HttpRequest req, CancellationToken ct)
    {
        try
        {
            // Read request body with an explicit cap
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

            // Canonicalize directly from raw bytes to enable duplicate-key detection and strict RFC8785 handling
            // This drops only the top-level "signature" property if present.
            byte[] canonicalBytes;
            try
            {
                canonicalBytes = CanonicalJson.CanonicaliseWithoutSignature(bytes);
            }
            catch (JsonException ex)
            {
                return Results.Problem(title: "Invalid JSON", detail: ex.Message, statusCode: (int)HttpStatusCode.BadRequest);
            }

            string canonicalJson = Encoding.UTF8.GetString(canonicalBytes);

            // Return the canonical JSON text with application/json content type
            return Results.Text(canonicalJson, MarketConstants.ContentTypes.Json, Encoding.UTF8);
        }
        catch (JsonException ex)
        {
            return Results.Problem(title: "Invalid JSON", detail: ex.Message, statusCode: (int)HttpStatusCode.BadRequest);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(title: "Invalid request", detail: ex.Message, statusCode: (int)HttpStatusCode.BadRequest);
        }
    }
}
