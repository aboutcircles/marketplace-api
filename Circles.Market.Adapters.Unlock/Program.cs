using System.Text.Json;
using System.Text.RegularExpressions;
using System.Numerics;
using Circles.Market.Adapters.Unlock;
using Circles.Market.Adapters.Unlock.Admin;
using Circles.Market.Adapters.Unlock.Auth;
using Circles.Market.Adapters.Unlock.Db;
using Circles.Market.Auth.Siwe;
using Circles.Market.Fulfillment.Core;
using Circles.Market.Shared;
using Circles.Market.Shared.Admin;
using Circles.Market.Shared.Auth;
using Npgsql;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Prometheus;
using Nethereum.Util;

var publicBuilder = WebApplication.CreateBuilder(args);

var connString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION");
if (string.IsNullOrWhiteSpace(connString))
{
    throw new InvalidOperationException("Missing required POSTGRES_CONNECTION environment variable.");
}

publicBuilder.Services.AddSingleton<UnlockDbBootstrapper>(sp =>
    new UnlockDbBootstrapper(connString, sp.GetRequiredService<ILogger<UnlockDbBootstrapper>>()));
publicBuilder.Services.AddSingleton<IUnlockMappingResolver>(sp =>
    new PostgresUnlockMappingResolver(connString, sp.GetRequiredService<ILogger<PostgresUnlockMappingResolver>>()));
publicBuilder.Services.AddSingleton<IUnlockMintStore>(_ => new PostgresUnlockMintStore(connString));
publicBuilder.Services.AddSingleton<IUnlockFulfillmentRunStore>(_ => new PostgresUnlockFulfillmentRunStore(connString));
publicBuilder.Services.AddSingleton<IFulfillmentRunStore>(sp => sp.GetRequiredService<IUnlockFulfillmentRunStore>());
publicBuilder.Services.AddSingleton<ITrustedCallerAuth>(sp =>
    new EnvTrustedCallerAuth(sp.GetRequiredService<ILogger<EnvTrustedCallerAuth>>()));
publicBuilder.Services.AddHttpClient();
publicBuilder.Services.AddSingleton<IUnlockClient, UnlockClient>();

var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
if (!string.IsNullOrEmpty(otlpEndpoint))
{
    publicBuilder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService(
            serviceName: Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ?? "market-adapter-unlock",
            serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown"))
        .WithTracing(tracing => tracing
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddOtlpExporter());
}

var publicApp = publicBuilder.Build();

using (var scope = publicApp.Services.CreateScope())
{
    var bootstrapper = scope.ServiceProvider.GetRequiredService<UnlockDbBootstrapper>();
    await bootstrapper.EnsureSchemaAsync();
}

var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
if (string.IsNullOrWhiteSpace(urls))
{
    int port = 5682;
    var portStr = Environment.GetEnvironmentVariable("PORT");
    if (!string.IsNullOrWhiteSpace(portStr) && int.TryParse(portStr, out var p) && p > 0 && p <= 65535)
    {
        port = p;
    }
    publicApp.Urls.Add($"http://0.0.0.0:{port}");
}

publicApp.UseHttpMetrics();

publicApp.MapGet("/health", async (CancellationToken ct) =>
{
    var checks = new Dictionary<string, string>();
    var allOk = true;

    try
    {
        await using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1";
        await cmd.ExecuteScalarAsync(ct);
        checks["postgres"] = "ok";
    }
    catch
    {
        checks["postgres"] = "failed";
        allOk = false;
    }

    return Results.Json(new { ok = allOk, checks }, statusCode: allOk ? 200 : 503);
});

publicApp.MapGet("/inventory/{chainId:long}/{seller}/{sku}", async (
    long chainId,
    string seller,
    string sku,
    HttpRequest request,
    ITrustedCallerAuth auth,
    IUnlockMappingResolver mappings,
    IUnlockMintStore mintStore,
    CancellationToken ct) =>
{
    if (!IsValidAddress(seller))
    {
        return Results.BadRequest(new { error = "Invalid seller address" });
    }

    if (string.IsNullOrWhiteSpace(sku))
    {
        return Results.BadRequest(new { error = "Missing sku" });
    }

    string? apiKey = request.Headers["X-Circles-Service-Key"].FirstOrDefault();
    var authRes = await auth.AuthorizeAsync(apiKey, "inventory", chainId, seller, ct);
    if (!authRes.Allowed)
    {
        return Results.Unauthorized();
    }

    var (mapped, entry) = await mappings.TryResolveAsync(chainId, seller, sku, ct);
    if (!mapped || entry is null)
    {
        return Results.NotFound(new { error = "No mapping for seller/sku", chainId, seller = seller.ToLowerInvariant(), sku = sku.ToLowerInvariant() });
    }

    var sold = await mintStore.CountSoldAsync(chainId, seller, sku, ct);
    var available = Math.Max(0L, entry.MaxSupply - sold);
    var payload = new Dictionary<string, object?>
    {
        ["@type"] = "QuantitativeValue",
        ["value"] = available,
        ["unitCode"] = "C62"
    };

    return Results.Json(payload, (JsonSerializerOptions?)Circles.Profiles.Models.JsonSerializerOptions.JsonLd);
});

publicApp.MapPost("/fulfill/{chainId:long}/{seller}", async (
    long chainId,
    string seller,
    HttpRequest request,
    ITrustedCallerAuth auth,
    IUnlockMappingResolver mappings,
    IUnlockMintStore mintStore,
    IUnlockFulfillmentRunStore runStore,
    IUnlockClient unlockClient,
    ILoggerFactory loggerFactory,
    CancellationToken ct) =>
{
    var log = loggerFactory.CreateLogger("UnlockFulfill");

    if (!IsValidAddress(seller))
    {
        return Results.BadRequest(new { error = "Invalid seller address" });
    }

    string? apiKey = request.Headers["X-Circles-Service-Key"].FirstOrDefault();
    var authRes = await auth.AuthorizeAsync(apiKey, "fulfill", chainId, seller, ct);
    if (!authRes.Allowed)
    {
        return Results.Unauthorized();
    }

    FulfillmentRequest? req;
    try
    {
        req = await JsonSerializer.DeserializeAsync<FulfillmentRequest>(request.Body, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }, ct);
    }
    catch (Exception ex)
    {
        log.LogWarning(ex, "Failed to parse request body");
        return Results.BadRequest(new { error = "Invalid JSON body" });
    }

    string? err = null;
    if (req is null || !req.TryNormalizeAndValidate(out err))
    {
        return Results.BadRequest(new { error = err ?? "Invalid request" });
    }

    if (string.IsNullOrWhiteSpace(req.Buyer) || !IsValidAddress(req.Buyer))
    {
        return Results.BadRequest(new { error = "buyer is required and must be a valid EVM address" });
    }

    string sellerNorm = seller.Trim().ToLowerInvariant();
    string buyer = req.Buyer.Trim().ToLowerInvariant();

    var gate = await FulfillmentRunGate.TryAcquireAsync(runStore, chainId, sellerNorm, req.PaymentReference, req.OrderId, ct);
    if (gate.State != FulfillmentRunGateState.Acquired)
    {
        if (gate.State == FulfillmentRunGateState.AlreadyProcessed)
        {
            var existing = await mintStore.GetByPaymentReferenceAsync(chainId, sellerNorm, req.PaymentReference, ct);
            return Results.Json(BuildReplayPayload(req, sellerNorm, existing, "Already processed"),
                (JsonSerializerOptions?)Circles.Profiles.Models.JsonSerializerOptions.JsonLd);
        }

        if (gate.State == FulfillmentRunGateState.InProgress)
        {
            var existing = await mintStore.GetByPaymentReferenceAsync(chainId, sellerNorm, req.PaymentReference, ct);
            return Results.Json(BuildReplayPayload(req, sellerNorm, existing, "Already in progress"),
                (JsonSerializerOptions?)Circles.Profiles.Models.JsonSerializerOptions.JsonLd);
        }

        return Results.Json(new
        {
            @context = new object[] { "https://schema.org/", "https://aboutcircles.com/contexts/circles-market/" },
            @type = "circles:UnlockFulfillmentResult",
            status = "error",
            orderId = req.OrderId,
            paymentReference = req.PaymentReference,
            seller = sellerNorm,
            message = "Could not acquire fulfillment lock"
        }, statusCode: 500);
    }

    var mapped = new List<(FulfillmentItem item, UnlockMappingEntry mapping)>();
    foreach (var item in req.Items)
    {
        var (isMapped, mapping) = await mappings.TryResolveAsync(chainId, sellerNorm, item.Sku, ct);
        if (isMapped && mapping is not null)
        {
            mapped.Add((item, mapping));
        }
    }

    if (mapped.Count == 0)
    {
        await runStore.MarkOkAsync(chainId, sellerNorm, req.PaymentReference, ct);
        return Results.Json(new
        {
            @context = new object[] { "https://schema.org/", "https://aboutcircles.com/contexts/circles-market/" },
            @type = "circles:UnlockFulfillmentResult",
            status = "notApplicable",
            orderId = req.OrderId,
            paymentReference = req.PaymentReference,
            seller = sellerNorm,
            buyer,
            tickets = Array.Empty<object>()
        }, (JsonSerializerOptions?)Circles.Profiles.Models.JsonSerializerOptions.JsonLd);
    }

    var distinctSkus = mapped.Select(m => m.item.Sku).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    if (distinctSkus.Count != 1)
    {
        await runStore.MarkErrorAsync(chainId, sellerNorm, req.PaymentReference, "Ambiguous mapping for fulfillment request", ct);
        return Results.Json(new
        {
            @context = new object[] { "https://schema.org/", "https://aboutcircles.com/contexts/circles-market/" },
            @type = "circles:UnlockFulfillmentResult",
            status = "ambiguous",
            orderId = req.OrderId,
            paymentReference = req.PaymentReference,
            seller = sellerNorm,
            buyer
        }, (JsonSerializerOptions?)Circles.Profiles.Models.JsonSerializerOptions.JsonLd);
    }

    var picked = mapped[0];
    var sold = await mintStore.CountSoldAsync(chainId, sellerNorm, picked.item.Sku, ct);
    if (sold >= picked.mapping.MaxSupply)
    {
        await runStore.MarkErrorAsync(chainId, sellerNorm, req.PaymentReference, "Unlock product is sold out", ct);
        return Results.Json(new
        {
            @context = new object[] { "https://schema.org/", "https://aboutcircles.com/contexts/circles-market/" },
            @type = "circles:UnlockFulfillmentResult",
            status = "depleted",
            orderId = req.OrderId,
            paymentReference = req.PaymentReference,
            seller = sellerNorm,
            sku = picked.item.Sku,
            buyer,
            message = "Unlock product is sold out"
        }, (JsonSerializerOptions?)Circles.Profiles.Models.JsonSerializerOptions.JsonLd);
    }

    var mint = await unlockClient.MintTicketAsync(
        picked.mapping,
        buyer,
        new UnlockRecipientInfo
        {
            Email = req.ContactPoint?.Email,
            GivenName = req.Customer?.GivenName,
            FamilyName = req.Customer?.FamilyName
        },
        ct);

    var payloadObj = BuildSuccessOrErrorPayload(req, sellerNorm, picked.item.Sku, picked.mapping, buyer, mint);
    var payloadJson = JsonSerializer.Serialize(payloadObj);

    await mintStore.UpsertMintAsync(new UnlockMintRecord
    {
        ChainId = chainId,
        SellerAddress = sellerNorm,
        PaymentReference = req.PaymentReference,
        OrderId = req.OrderId,
        Sku = picked.item.Sku,
        BuyerAddress = buyer,
        LockAddress = picked.mapping.LockAddress,
        TransactionHash = mint.TransactionHash,
        KeyId = mint.KeyId?.ToString(),
        ExpirationUnix = mint.ExpirationUnix,
        Status = mint.Success ? "ok" : "error",
        Warning = mint.Warnings.Count == 0 ? null : string.Join(",", mint.Warnings),
        Error = mint.Error,
        ResponseJson = payloadJson
    }, ct);

    if (!mint.Success)
    {
        await runStore.MarkErrorAsync(chainId, sellerNorm, req.PaymentReference, mint.Error ?? "Unlock mint failed", ct);
        return Results.Json(payloadObj, (JsonSerializerOptions?)Circles.Profiles.Models.JsonSerializerOptions.JsonLd, statusCode: 500);
    }

    await runStore.MarkOkAsync(chainId, sellerNorm, req.PaymentReference, ct);
    return Results.Json(payloadObj, (JsonSerializerOptions?)Circles.Profiles.Models.JsonSerializerOptions.JsonLd);
});

publicApp.MapGet("/tickets/{chainId:long}/{seller}/{paymentReference}/qrcode", async (
    long chainId,
    string seller,
    string paymentReference,
    HttpRequest request,
    ITrustedCallerAuth auth,
    IUnlockMintStore mintStore,
    IUnlockMappingResolver mappings,
    IUnlockClient unlockClient,
    ILoggerFactory loggerFactory,
    CancellationToken ct) =>
{
    if (!IsValidAddress(seller))
    {
        return Results.BadRequest(new { error = "Invalid seller address" });
    }

    if (string.IsNullOrWhiteSpace(paymentReference))
    {
        return Results.BadRequest(new { error = "paymentReference is required" });
    }

    string? apiKey = request.Headers["X-Circles-Service-Key"].FirstOrDefault();
    var authRes = await auth.AuthorizeAsync(apiKey, "ticket", chainId, seller, ct);
    if (!authRes.Allowed)
    {
        return Results.Unauthorized();
    }

    var sellerNorm = seller.Trim().ToLowerInvariant();
    var paymentRefNorm = paymentReference.Trim();
    var existing = await mintStore.GetByPaymentReferenceAsync(chainId, sellerNorm, paymentRefNorm, ct);
    if (existing is null)
    {
        return Results.NotFound(new { error = "Mint record not found" });
    }

    var persistedQrCode = TryGetQrCode(existing.ResponseJson);
    if (!string.IsNullOrWhiteSpace(persistedQrCode))
    {
        return Results.Json(new { qrcode = persistedQrCode });
    }

    if (string.IsNullOrWhiteSpace(existing.KeyId))
    {
        return Results.UnprocessableEntity(new { error = "Mint record does not include keyId" });
    }

    if (!BigInteger.TryParse(existing.KeyId, out var keyId))
    {
        return Results.UnprocessableEntity(new { error = "Stored keyId is invalid" });
    }

    if (string.IsNullOrWhiteSpace(existing.Sku))
    {
        return Results.UnprocessableEntity(new { error = "Mint record does not include sku" });
    }

    var (mapped, mapping) = await mappings.TryResolveAsync(chainId, sellerNorm, existing.Sku, ct);
    if (!mapped || mapping is null)
    {
        return Results.NotFound(new { error = "No mapping for seller/sku", chainId, seller = sellerNorm, sku = existing.Sku });
    }

    string qrcode;
    try
    {
        qrcode = await unlockClient.GetTicketQrCodeDataUrlAsync(mapping, keyId, ct);
    }
    catch (Exception ex)
    {
        return Results.Problem(
            detail: $"Failed to fetch QR code from Locksmith: {ex.Message}",
            statusCode: StatusCodes.Status502BadGateway,
            title: "QR fetch failed");
    }

    var patchedResponseJson = UpsertQrCodeIntoResponseJson(existing.ResponseJson, qrcode, loggerFactory.CreateLogger("UnlockQrUpsert"));
    await mintStore.UpsertMintAsync(new UnlockMintRecord
    {
        ChainId = existing.ChainId,
        SellerAddress = existing.SellerAddress,
        PaymentReference = existing.PaymentReference,
        OrderId = existing.OrderId,
        Sku = existing.Sku,
        BuyerAddress = existing.BuyerAddress,
        LockAddress = existing.LockAddress,
        TransactionHash = existing.TransactionHash,
        KeyId = existing.KeyId,
        ExpirationUnix = existing.ExpirationUnix,
        Status = existing.Status,
        Warning = existing.Warning,
        Error = existing.Error,
        ResponseJson = patchedResponseJson
    }, ct);

    return Results.Json(new { qrcode });
});

publicApp.MapMetrics();

var adminBuilder = WebApplication.CreateBuilder(args);
adminBuilder.Logging.ClearProviders();
adminBuilder.Logging.AddConsole();
adminBuilder.WebHost.UseUrls($"http://0.0.0.0:{AdminPortConfig.GetAdminPort("UNLOCK_ADMIN_PORT", 5692)}");
adminBuilder.Services.AddAuthServiceJwks();
adminBuilder.Services.AddSingleton<IUnlockMappingResolver>(sp =>
    new PostgresUnlockMappingResolver(connString, sp.GetRequiredService<ILogger<PostgresUnlockMappingResolver>>()));

var adminApp = adminBuilder.Build();
adminApp.UseAuthentication();
adminApp.UseAuthorization();
adminApp.MapUnlockAdminApi("/admin");

await Task.WhenAll(publicApp.RunAsync(), adminApp.RunAsync());

static bool IsValidAddress(string value)
{
    return !string.IsNullOrWhiteSpace(value)
           && AddressUtil.Current.IsValidEthereumAddressHexFormat(value.Trim());
}

static object BuildReplayPayload(FulfillmentRequest req, string seller, UnlockMintRecord? existing, string message)
{
    return new
    {
        @context = new object[] { "https://schema.org/", "https://aboutcircles.com/contexts/circles-market/" },
        @type = "circles:UnlockFulfillmentResult",
        status = "ok",
        orderId = req.OrderId,
        paymentReference = req.PaymentReference,
        seller,
        sku = existing?.Sku,
        buyer = existing?.BuyerAddress ?? req.Buyer,
        lockAddress = existing?.LockAddress,
        transactionHash = existing?.TransactionHash,
        keyId = existing?.KeyId,
        expirationUnix = existing?.ExpirationUnix,
        message,
        warnings = TryGetWarnings(existing?.ResponseJson, existing?.Warning),
        emailDeliveryAttempted = TryGetBoolProperty(existing?.ResponseJson, "emailDeliveryAttempted"),
        emailDeliverySent = TryGetBoolProperty(existing?.ResponseJson, "emailDeliverySent"),
        emailRecipient = TryGetStringProperty(existing?.ResponseJson, "emailRecipient"),
        emailDeliveryError = TryGetStringProperty(existing?.ResponseJson, "emailDeliveryError"),
        userMetadataUpdateAttempted = TryGetBoolProperty(existing?.ResponseJson, "userMetadataUpdateAttempted"),
        userMetadataUpdateSuccess = TryGetBoolProperty(existing?.ResponseJson, "userMetadataUpdateSuccess"),
        userMetadataUpdateError = TryGetStringProperty(existing?.ResponseJson, "userMetadataUpdateError"),
        metadataUpdateAttempted = TryGetBoolProperty(existing?.ResponseJson, "metadataUpdateAttempted"),
        metadataUpdateSuccess = TryGetBoolProperty(existing?.ResponseJson, "metadataUpdateSuccess"),
        metadataUpdateError = TryGetStringProperty(existing?.ResponseJson, "metadataUpdateError"),
        ticket = TryGetTicket(existing?.ResponseJson),
        qrcode = TryGetQrCode(existing?.ResponseJson)
    };
}

static object BuildSuccessOrErrorPayload(
    FulfillmentRequest req,
    string seller,
    string sku,
    UnlockMappingEntry mapping,
    string buyer,
    UnlockMintOutcome mint)
{
    return new
    {
        @context = new object[] { "https://schema.org/", "https://aboutcircles.com/contexts/circles-market/" },
        @type = "circles:UnlockFulfillmentResult",
        status = mint.Success ? "ok" : "error",
        orderId = req.OrderId,
        paymentReference = req.PaymentReference,
        seller,
        sku,
        buyer,
        lockAddress = mapping.LockAddress,
        transactionHash = mint.TransactionHash,
        keyId = mint.KeyId?.ToString(),
        expirationUnix = mint.ExpirationUnix,
        warnings = mint.Warnings,
        message = mint.Success ? "Unlock ticket minted" : mint.Error,
        emailDeliveryAttempted = mint.EmailDeliveryAttempted,
        emailDeliverySent = mint.EmailDeliverySent,
        emailRecipient = mint.EmailRecipient,
        emailDeliveryError = mint.EmailDeliveryError,
        userMetadataUpdateAttempted = mint.UserMetadataUpdateAttempted,
        userMetadataUpdateSuccess = mint.UserMetadataUpdateSuccess,
        userMetadataUpdateError = mint.UserMetadataUpdateError,
        metadataUpdateAttempted = mint.MetadataUpdateAttempted,
        metadataUpdateSuccess = mint.MetadataUpdateSuccess,
        metadataUpdateError = mint.MetadataUpdateError,
        ticket = mint.Ticket,
        qrcode = mint.QrCodeDataUrl
    };
}

static string[] TryGetWarnings(string? responseJson, string? fallbackWarning)
{
    if (!string.IsNullOrWhiteSpace(responseJson))
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (doc.RootElement.TryGetProperty("warnings", out var warnings) && warnings.ValueKind == JsonValueKind.Array)
            {
                return warnings
                    .EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x!)
                    .ToArray();
            }
        }
        catch
        {
        }
    }

    return string.IsNullOrWhiteSpace(fallbackWarning)
        ? Array.Empty<string>()
        : fallbackWarning
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

static JsonElement? TryGetTicket(string? responseJson)
{
    if (string.IsNullOrWhiteSpace(responseJson)) return null;
    try
    {
        using var doc = JsonDocument.Parse(responseJson);
        if (doc.RootElement.TryGetProperty("ticket", out var ticket))
        {
            return ticket.Clone();
        }
    }
    catch
    {
    }

    return null;
}

static string? TryGetQrCode(string? responseJson)
{
    if (string.IsNullOrWhiteSpace(responseJson)) return null;
    try
    {
        using var doc = JsonDocument.Parse(responseJson);
        if (doc.RootElement.TryGetProperty("qrcode", out var qrCode) && qrCode.ValueKind == JsonValueKind.String)
        {
            var value = qrCode.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }
    catch
    {
    }

    return null;
}

static bool? TryGetBoolProperty(string? responseJson, string propertyName)
{
    if (string.IsNullOrWhiteSpace(responseJson)) return null;
    try
    {
        using var doc = JsonDocument.Parse(responseJson);
        if (doc.RootElement.TryGetProperty(propertyName, out var prop) &&
            (prop.ValueKind == JsonValueKind.True || prop.ValueKind == JsonValueKind.False))
        {
            return prop.GetBoolean();
        }
    }
    catch
    {
    }

    return null;
}

static string? TryGetStringProperty(string? responseJson, string propertyName)
{
    if (string.IsNullOrWhiteSpace(responseJson)) return null;
    try
    {
        using var doc = JsonDocument.Parse(responseJson);
        if (doc.RootElement.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            var value = prop.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }
    catch
    {
    }

    return null;
}

static string UpsertQrCodeIntoResponseJson(string? responseJson, string qrcodeDataUrl, ILogger? log = null)
{
    if (string.IsNullOrWhiteSpace(responseJson))
    {
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["qrcode"] = qrcodeDataUrl
        });
    }

    try
    {
        using var doc = JsonDocument.Parse(responseJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["qrcode"] = qrcodeDataUrl
            });
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(prop.Name, "qrcode", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                prop.WriteTo(writer);
            }

            writer.WriteString("qrcode", qrcodeDataUrl);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
    catch (Exception ex)
    {
        log?.LogWarning(ex, "Failed to upsert qrcode into stored responseJson; replacing with minimal JSON payload");
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["qrcode"] = qrcodeDataUrl
        });
    }
}
