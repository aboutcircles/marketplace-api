using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using Circles.Market.Adapters.Unlock;
using Circles.Market.Adapters.Unlock.Admin;
using Circles.Market.Adapters.Unlock.Auth;
using Circles.Market.Adapters.Unlock.Db;
using Circles.Market.Auth.Siwe;
using Circles.Market.Fulfillment.Core;
using Circles.Market.Shared;
using Circles.Market.Shared.Admin;
using Circles.Market.Shared.Auth;
using Nethereum.Util;
using Npgsql;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Prometheus;

var publicBuilder = WebApplication.CreateBuilder(args);

var connString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION");
if (string.IsNullOrWhiteSpace(connString))
    throw new InvalidOperationException("Missing required POSTGRES_CONNECTION environment variable.");

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
publicBuilder.Services.AddSingleton<ILocksmithAuthProvider, LocksmithAuthProvider>();
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

if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    var port = 5682;
    var portStr = Environment.GetEnvironmentVariable("PORT");
    if (!string.IsNullOrWhiteSpace(portStr) && int.TryParse(portStr, out var p) && p is > 0 and <= 65535)
        port = p;
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
    if (!IsValidAddress(seller)) return Results.BadRequest(new { error = "Invalid seller address" });
    if (string.IsNullOrWhiteSpace(sku)) return Results.BadRequest(new { error = "Missing sku" });

    var apiKey = request.Headers["X-Circles-Service-Key"].FirstOrDefault();
    var authRes = await auth.AuthorizeAsync(apiKey, "inventory", chainId, seller, ct);
    if (!authRes.Allowed) return Results.Unauthorized();

    var (mapped, entry) = await mappings.TryResolveAsync(chainId, seller, sku, ct);
    if (!mapped || entry is null)
        return Results.NotFound(new { error = "No mapping for seller/sku", chainId, seller = seller.ToLowerInvariant(), sku = sku.ToLowerInvariant() });

    var sold = await mintStore.CountSoldAsync(chainId, seller, sku, ct);
    var available = Math.Max(0L, entry.MaxSupply - sold);
    return Results.Json(new Dictionary<string, object?>
    {
        ["@type"] = "QuantitativeValue",
        ["value"] = available,
        ["unitCode"] = "C62"
    }, (JsonSerializerOptions?)Circles.Profiles.Models.JsonSerializerOptions.JsonLd);
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

    if (!IsValidAddress(seller)) return Results.BadRequest(new { error = "Invalid seller address" });

    var apiKey = request.Headers["X-Circles-Service-Key"].FirstOrDefault();
    var authRes = await auth.AuthorizeAsync(apiKey, "fulfill", chainId, seller, ct);
    if (!authRes.Allowed) return Results.Unauthorized();

    FulfillmentRequest? req;
    try
    {
        req = await JsonSerializer.DeserializeAsync<FulfillmentRequest>(request.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
    }
    catch (Exception ex)
    {
        log.LogWarning(ex, "Failed to parse request body");
        return Results.BadRequest(new { error = "Invalid JSON body" });
    }

    if (req is null)
        return Results.BadRequest(new { error = "Invalid request" });

    string? validationError = null;
    if (!req.TryNormalizeAndValidate(out validationError))
        return Results.BadRequest(new { error = validationError ?? "Invalid request" });
    if (string.IsNullOrWhiteSpace(req.Buyer) || !IsValidAddress(req.Buyer))
        return Results.BadRequest(new { error = "buyer is required and must be a valid EVM address" });

    var sellerNorm = seller.Trim().ToLowerInvariant();
    var buyer = req.Buyer.Trim().ToLowerInvariant();

    var gate = await FulfillmentRunGate.TryAcquireAsync(runStore, chainId, sellerNorm, req.PaymentReference, req.OrderId, ct);
    if (gate.State != FulfillmentRunGateState.Acquired)
    {
        var existing = await mintStore.GetByPaymentReferenceAsync(chainId, sellerNorm, req.PaymentReference, ct);
        if (gate.State is FulfillmentRunGateState.AlreadyProcessed or FulfillmentRunGateState.InProgress)
        {
            var tickets = await mintStore.ListTicketsByPaymentReferenceAsync(chainId, sellerNorm, req.PaymentReference, ct);
            return Results.Json(BuildReplayPayload(req, sellerNorm, existing, tickets, gate.State == FulfillmentRunGateState.AlreadyProcessed ? "Already processed" : "Already in progress", log),
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

    var mapped = new List<(FulfillmentItem Item, UnlockMappingEntry Mapping, int Quantity)>();
    foreach (var group in req.Items.GroupBy(i => i.Sku, StringComparer.OrdinalIgnoreCase))
    {
        var (isMapped, mapping) = await mappings.TryResolveAsync(chainId, sellerNorm, group.Key, ct);
        if (!isMapped || mapping is null) continue;

        if (group.Any(x => x.Quantity <= 0))
            return Results.BadRequest(new { error = $"Quantity must be > 0 for sku {group.Key}" });
        if (group.Any(x => x.Quantity != decimal.Truncate(x.Quantity)))
            return Results.BadRequest(new { error = $"Quantity must be an integer for sku {group.Key}" });

        decimal qtyRaw = group.Sum(x => x.Quantity);
        if (qtyRaw <= 0) return Results.BadRequest(new { error = $"Quantity must be > 0 for sku {group.Key}" });
        if (qtyRaw != decimal.Truncate(qtyRaw)) return Results.BadRequest(new { error = $"Quantity must be an integer for sku {group.Key}" });
        var quantity = (int)qtyRaw;
        if (quantity <= 0) return Results.BadRequest(new { error = $"Quantity must be > 0 for sku {group.Key}" });

        mapped.Add((group.First(), mapping, quantity));
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

    if (mapped.Count != 1)
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
    var sold = await mintStore.CountSoldAsync(chainId, sellerNorm, picked.Item.Sku, ct);
    if (sold + picked.Quantity > picked.Mapping.MaxSupply)
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
            sku = picked.Item.Sku,
            buyer,
            quantity = picked.Quantity,
            message = "Unlock product is sold out"
        }, (JsonSerializerOptions?)Circles.Profiles.Models.JsonSerializerOptions.JsonLd);
    }

    var mint = await unlockClient.MintTicketsAsync(picked.Mapping, buyer, picked.Quantity, ct);
    var payloadObj = BuildSuccessOrErrorPayload(req, sellerNorm, picked.Item.Sku, picked.Mapping, buyer, picked.Quantity, mint);
    var payloadJson = JsonSerializer.Serialize(payloadObj);

    var summaryRecord = new UnlockMintRecord
    {
        ChainId = chainId,
        SellerAddress = sellerNorm,
        PaymentReference = req.PaymentReference,
        OrderId = req.OrderId,
        Sku = picked.Item.Sku,
        BuyerAddress = buyer,
        LockAddress = picked.Mapping.LockAddress,
        Quantity = picked.Quantity,
        TransactionHash = mint.TransactionHash,
        KeyId = mint.Tickets.Count > 0 ? mint.Tickets[0].KeyId.ToString() : null,
        ExpirationUnix = mint.ExpirationUnix,
        Status = mint.Success ? "ok" : "error",
        Warning = mint.Warnings.Count == 0 ? null : string.Join(",", mint.Warnings),
        Error = mint.Error,
        ResponseJson = payloadJson
    };

    var ticketRecords = mint.Tickets.Select(t => new UnlockMintTicketRecord
    {
        ChainId = chainId,
        SellerAddress = sellerNorm,
        PaymentReference = req.PaymentReference,
        TicketIndex = t.TicketIndex,
        OrderId = req.OrderId,
        Sku = picked.Item.Sku,
        BuyerAddress = buyer,
        LockAddress = picked.Mapping.LockAddress,
        TransactionHash = t.TransactionHash,
        KeyId = t.KeyId.ToString(),
        ExpirationUnix = t.ExpirationUnix,
        Status = string.IsNullOrWhiteSpace(t.Error) ? "ok" : "error",
        Warning = t.Warnings.Count == 0 ? null : string.Join(",", t.Warnings),
        Error = t.Error,
        TicketJson = t.Ticket?.GetRawText(),
        QrcodeDataUrl = t.QrCodeDataUrl
    }).ToList();

    await mintStore.UpsertMintWithTicketsAsync(summaryRecord, ticketRecords, ct);

    if (!mint.Success)
    {
        await runStore.MarkErrorAsync(chainId, sellerNorm, req.PaymentReference, mint.Error ?? "Unlock mint failed", ct);
        return Results.Json(payloadObj, (JsonSerializerOptions?)Circles.Profiles.Models.JsonSerializerOptions.JsonLd, statusCode: 500);
    }

    await runStore.MarkOkAsync(chainId, sellerNorm, req.PaymentReference, ct);
    return Results.Json(payloadObj, (JsonSerializerOptions?)Circles.Profiles.Models.JsonSerializerOptions.JsonLd);
});

publicApp.MapGet("/tickets/{chainId:long}/{seller}/{paymentReference}", async (
    long chainId,
    string seller,
    string paymentReference,
    HttpRequest request,
    ITrustedCallerAuth auth,
    IUnlockMintStore mintStore,
    ILoggerFactory loggerFactory,
    CancellationToken ct) =>
{
    var log = loggerFactory.CreateLogger("UnlockTicketsRead");

    if (!IsValidAddress(seller)) return Results.BadRequest(new { error = "Invalid seller address" });
    if (string.IsNullOrWhiteSpace(paymentReference)) return Results.BadRequest(new { error = "paymentReference is required" });

    var apiKey = request.Headers["X-Circles-Service-Key"].FirstOrDefault();
    var authRes = await auth.AuthorizeAsync(apiKey, "ticket", chainId, seller, ct);
    if (!authRes.Allowed)
    {
        log.LogWarning("Ticket read denied by trusted caller auth. chain={Chain} seller={Seller}", chainId, seller);
        return Results.Unauthorized();
    }

    var sellerNorm = seller.Trim().ToLowerInvariant();
    var paymentRefNorm = paymentReference.Trim();
    var existing = await mintStore.GetByPaymentReferenceAsync(chainId, sellerNorm, paymentRefNorm, ct);
    if (existing is null) return Results.NotFound(new { error = "Mint record not found" });

    var tickets = await mintStore.ListTicketsByPaymentReferenceAsync(chainId, sellerNorm, paymentRefNorm, ct);
    var status = NormalizeFulfillmentStatus(existing.Status);
    return Results.Json(BuildTicketsPayload(existing, tickets, status, log: log), (JsonSerializerOptions?)Circles.Profiles.Models.JsonSerializerOptions.JsonLd);
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
    var log = loggerFactory.CreateLogger("UnlockQrcodeLegacy");

    if (!IsValidAddress(seller)) return Results.BadRequest(new { error = "Invalid seller address" });
    if (string.IsNullOrWhiteSpace(paymentReference)) return Results.BadRequest(new { error = "paymentReference is required" });

    var apiKey = request.Headers["X-Circles-Service-Key"].FirstOrDefault();
    var authRes = await auth.AuthorizeAsync(apiKey, "ticket", chainId, seller, ct);
    if (!authRes.Allowed) return Results.Unauthorized();

    var sellerNorm = seller.Trim().ToLowerInvariant();
    var paymentRefNorm = paymentReference.Trim();
    var existing = await mintStore.GetByPaymentReferenceAsync(chainId, sellerNorm, paymentRefNorm, ct);
    if (existing is null) return Results.NotFound(new { error = "Mint record not found" });

    var tickets = await mintStore.ListTicketsByPaymentReferenceAsync(chainId, sellerNorm, paymentRefNorm, ct);
    if (tickets.Count > 1 || existing.Quantity > 1)
        return Results.Conflict(new { error = "qrcode endpoint is only valid for single-ticket records" });

    if (tickets.Count == 1 && !string.IsNullOrWhiteSpace(tickets[0].QrcodeDataUrl))
        return Results.Json(new { qrcode = tickets[0].QrcodeDataUrl });

    var sourceKeyId = tickets.Count == 1 && !string.IsNullOrWhiteSpace(tickets[0].KeyId)
        ? tickets[0].KeyId
        : existing.KeyId;

    if (string.IsNullOrWhiteSpace(sourceKeyId)) return Results.UnprocessableEntity(new { error = "Mint record does not include keyId" });
    if (!BigInteger.TryParse(sourceKeyId, out var keyId)) return Results.UnprocessableEntity(new { error = "Stored keyId is invalid" });
    if (string.IsNullOrWhiteSpace(existing.Sku)) return Results.UnprocessableEntity(new { error = "Mint record does not include sku" });

    var (mapped, mapping) = await mappings.TryResolveAsync(chainId, sellerNorm, existing.Sku, ct);
    if (!mapped || mapping is null) return Results.NotFound(new { error = "No mapping for seller/sku", chainId, seller = sellerNorm, sku = existing.Sku });

    string qrcode;
    JsonElement? ticket;
    IReadOnlyList<string> warnings;
    try
    {
        var result = await unlockClient.GetOrGenerateQrCodeAsync(mapping, keyId, ct);
        qrcode = result.QrCodeDataUrl ?? string.Empty;
        ticket = result.Ticket;
        warnings = result.Warnings;
        if (string.IsNullOrWhiteSpace(qrcode)) return Results.Problem("QR code not ready", statusCode: 502, title: "QR fetch failed");
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: $"Failed to fetch QR code from Locksmith: {ex.Message}", statusCode: 502, title: "QR fetch failed");
    }

    var mergedWarnings = MergeWarningCodes(existing.Warning, warnings);

    await mintStore.UpsertMintTicketsAsync(new[]
    {
        new UnlockMintTicketRecord
        {
            ChainId = existing.ChainId,
            SellerAddress = existing.SellerAddress,
            PaymentReference = existing.PaymentReference,
            TicketIndex = tickets.Count == 1 ? tickets[0].TicketIndex : 0,
            OrderId = existing.OrderId,
            Sku = existing.Sku,
            BuyerAddress = existing.BuyerAddress,
            LockAddress = existing.LockAddress,
            TransactionHash = existing.TransactionHash,
            KeyId = sourceKeyId,
            ExpirationUnix = existing.ExpirationUnix,
            Status = existing.Status,
            Warning = mergedWarnings.Count == 0 ? null : string.Join(",", mergedWarnings),
            Error = existing.Error,
            TicketJson = ticket?.GetRawText(),
            QrcodeDataUrl = qrcode
        }
    }, ct);

    var patchedResponseJson = UpsertQrCodeIntoResponseJson(existing.ResponseJson, qrcode, log);
    await mintStore.UpsertMintAsync(new UnlockMintRecord
    {
        ChainId = existing.ChainId,
        SellerAddress = existing.SellerAddress,
        PaymentReference = existing.PaymentReference,
        OrderId = existing.OrderId,
        Sku = existing.Sku,
        BuyerAddress = existing.BuyerAddress,
        LockAddress = existing.LockAddress,
        Quantity = existing.Quantity,
        TransactionHash = existing.TransactionHash,
        KeyId = sourceKeyId,
        ExpirationUnix = existing.ExpirationUnix,
        Status = existing.Status,
        Warning = mergedWarnings.Count == 0 ? null : string.Join(",", mergedWarnings),
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
    => !string.IsNullOrWhiteSpace(value) && AddressUtil.Current.IsValidEthereumAddressHexFormat(value.Trim());

static object BuildReplayPayload(FulfillmentRequest req, string seller, UnlockMintRecord? existing, IReadOnlyList<UnlockMintTicketRecord> tickets, string message, ILogger log)
{
    var status = NormalizeFulfillmentStatus(existing?.Status);

    return BuildTicketsPayload(existing, tickets, status, req.OrderId, req.PaymentReference, seller, req.Buyer, message, log);
}

static object BuildSuccessOrErrorPayload(FulfillmentRequest req, string seller, string sku, UnlockMappingEntry mapping, string buyer, int quantity, UnlockMintOutcome mint)
{
    var tickets = mint.Tickets.Select(t => new
    {
        ticketIndex = t.TicketIndex,
        keyId = t.KeyId.ToString(),
        expirationUnix = t.ExpirationUnix,
        transactionHash = t.TransactionHash,
        ticket = t.Ticket,
        qrcode = t.QrCodeDataUrl,
        warnings = t.Warnings,
        error = t.Error
    }).ToList();

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
        quantity,
        warnings = mint.Warnings,
        message = mint.Success ? "Unlock tickets minted" : mint.Error,
        tickets
    };
}

static object BuildTicketsPayload(
    UnlockMintRecord? summary,
    IReadOnlyList<UnlockMintTicketRecord> tickets,
    string status,
    string? orderId = null,
    string? paymentReference = null,
    string? seller = null,
    string? buyer = null,
    string? message = null,
    ILogger? log = null)
{
    orderId ??= summary?.OrderId;
    paymentReference ??= summary?.PaymentReference;
    seller ??= summary?.SellerAddress;
    buyer ??= summary?.BuyerAddress;

    var responseTickets = tickets.Select(t => new
    {
        ticketIndex = t.TicketIndex,
        keyId = t.KeyId,
        expirationUnix = t.ExpirationUnix,
        transactionHash = t.TransactionHash,
        ticket = TryParseJsonElement(t.TicketJson, log),
        qrcode = t.QrcodeDataUrl,
        warnings = ParseWarnings(t.Warning),
        error = t.Error
    }).ToList();

    if (responseTickets.Count == 0 && summary is not null)
    {
        responseTickets.Add(new
        {
            ticketIndex = 0,
            keyId = summary.KeyId,
            expirationUnix = summary.ExpirationUnix,
            transactionHash = summary.TransactionHash,
            ticket = TryGetTicket(summary.ResponseJson, log),
            qrcode = TryGetQrCode(summary.ResponseJson, log),
            warnings = ParseWarnings(summary.Warning),
            error = summary.Error
        });
    }

    return new
    {
        @context = new object[] { "https://schema.org/", "https://aboutcircles.com/contexts/circles-market/" },
        @type = "circles:UnlockFulfillmentResult",
        status,
        orderId,
        paymentReference,
        seller,
        buyer,
        sku = summary?.Sku,
        lockAddress = summary?.LockAddress,
        quantity = summary?.Quantity ?? Math.Max(responseTickets.Count, 1),
        warnings = ParseWarnings(summary?.Warning),
        message,
        tickets = responseTickets
    };
}

static string[] ParseWarnings(string? csv)
    => string.IsNullOrWhiteSpace(csv)
        ? Array.Empty<string>()
        : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

static JsonElement? TryParseJsonElement(string? json, ILogger? log)
{
    if (string.IsNullOrWhiteSpace(json)) return null;
    try
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
    catch (JsonException ex)
    {
        log?.LogWarning(ex, "Failed to parse ticket_json while building unlock response payload");
        Activity.Current?.AddEvent(new ActivityEvent("unlock.json.parse_failed"));
        Activity.Current?.SetTag("unlock.json.parse_failed", true);
        return null;
    }
}

static JsonElement? TryGetTicket(string? responseJson, ILogger? log)
{
    if (string.IsNullOrWhiteSpace(responseJson)) return null;
    try
    {
        using var doc = JsonDocument.Parse(responseJson);
        if (doc.RootElement.TryGetProperty("ticket", out var ticket))
            return ticket.Clone();
    }
    catch (JsonException ex)
    {
        log?.LogWarning(ex, "Malformed response_json while extracting ticket for legacy payload fallback");
        Activity.Current?.AddEvent(new ActivityEvent("unlock.json.ticket_extract_failed"));
    }

    return null;
}

static string? TryGetQrCode(string? responseJson, ILogger? log)
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
    catch (JsonException ex)
    {
        log?.LogWarning(ex, "Malformed response_json while extracting qrcode for legacy payload fallback");
        Activity.Current?.AddEvent(new ActivityEvent("unlock.json.qrcode_extract_failed"));
    }

    return null;
}

static string NormalizeFulfillmentStatus(string? status)
    => status?.Trim().ToLowerInvariant() switch
    {
        "ok" => "ok",
        "error" => "error",
        _ => "inProgress"
    };

static IReadOnlyList<string> MergeWarningCodes(string? existingCsv, IReadOnlyList<string> additional)
{
    var all = ParseWarnings(existingCsv)
        .Concat(additional)
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Select(x => x.Trim())
        .Distinct(StringComparer.Ordinal)
        .ToList();

    return all;
}

static string UpsertQrCodeIntoResponseJson(string? responseJson, string qrcodeDataUrl, ILogger log)
{
    if (string.IsNullOrWhiteSpace(responseJson))
        return JsonSerializer.Serialize(new Dictionary<string, object?> { ["qrcode"] = qrcodeDataUrl, ["warnings"] = new[] { "responseJsonMalformed" } });

    try
    {
        using var doc = JsonDocument.Parse(responseJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("responseJson root is not object");

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(prop.Name, "qrcode", StringComparison.OrdinalIgnoreCase)) continue;
                prop.WriteTo(writer);
            }

            writer.WriteString("qrcode", qrcodeDataUrl);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
    catch (JsonException ex)
    {
        log.LogWarning(ex, "response_json malformed during QR upsert; replacing payload and persisting warning code responseJsonMalformed");
        Activity.Current?.AddEvent(new ActivityEvent("unlock.json.malformed_replaced"));
        Activity.Current?.SetTag("unlock.json.error", ex.Message);
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["qrcode"] = qrcodeDataUrl,
            ["warnings"] = new[] { "responseJsonMalformed" }
        });
    }
}
