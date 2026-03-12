using Circles.Market.Shared.Admin;
using Microsoft.AspNetCore.Authorization;

namespace Circles.Market.Adapters.Unlock.Admin;

public static class AdminEndpoints
{
    public static void MapUnlockAdminApi(this WebApplication app, string adminBasePath)
    {
        var group = app.MapGroup(adminBasePath).RequireAuthorization();

        group.MapGet("/health", () => Results.Json(new { ok = true }));

        group.MapGet("/mappings", async (IUnlockMappingResolver resolver, CancellationToken ct) =>
        {
            var rows = await resolver.ListMappingsAsync(ct);
            var result = rows.Select(MapToDto).ToList();
            return Results.Json(result);
        });

        group.MapPut("/mappings", async (UnlockMappingUpsertRequest req, IUnlockMappingResolver resolver, CancellationToken ct) =>
        {
            if (req.ChainId <= 0) return Results.BadRequest(new { error = "chainId must be > 0" });
            if (string.IsNullOrWhiteSpace(req.Seller) || string.IsNullOrWhiteSpace(req.Sku))
                return Results.BadRequest(new { error = "seller and sku are required" });
            if (string.IsNullOrWhiteSpace(req.LockAddress))
                return Results.BadRequest(new { error = "lockAddress is required" });
            if (string.IsNullOrWhiteSpace(req.RpcUrl))
                return Results.BadRequest(new { error = "rpcUrl is required" });
            if (string.IsNullOrWhiteSpace(req.ServicePrivateKey))
                return Results.BadRequest(new { error = "servicePrivateKey is required" });
            if (req.MaxSupply < 0)
                return Results.BadRequest(new { error = "maxSupply must be >= 0" });

            bool hasDuration = req.DurationSeconds.HasValue;
            bool hasExpiration = req.ExpirationUnix.HasValue;
            if (hasDuration == hasExpiration)
                return Results.BadRequest(new { error = "Provide exactly one of durationSeconds or expirationUnix" });
            if (hasDuration && req.DurationSeconds <= 0)
                return Results.BadRequest(new { error = "durationSeconds must be > 0" });
            if (hasExpiration && req.ExpirationUnix <= 0)
                return Results.BadRequest(new { error = "expirationUnix must be > 0" });

            string keyManagerMode = string.IsNullOrWhiteSpace(req.KeyManagerMode)
                ? "buyer"
                : req.KeyManagerMode.Trim().ToLowerInvariant();

            if (keyManagerMode is not ("buyer" or "service" or "fixed"))
                return Results.BadRequest(new { error = "keyManagerMode must be one of: buyer, service, fixed" });
            if (keyManagerMode == "fixed" && string.IsNullOrWhiteSpace(req.FixedKeyManager))
                return Results.BadRequest(new { error = "fixedKeyManager is required when keyManagerMode=fixed" });

            var entry = new UnlockMappingEntry
            {
                ChainId = req.ChainId,
                Seller = req.Seller.Trim().ToLowerInvariant(),
                Sku = req.Sku.Trim().ToLowerInvariant(),
                LockAddress = req.LockAddress.Trim(),
                RpcUrl = req.RpcUrl.Trim(),
                ServicePrivateKey = req.ServicePrivateKey.Trim(),
                DurationSeconds = req.DurationSeconds,
                ExpirationUnix = req.ExpirationUnix,
                KeyManagerMode = keyManagerMode,
                FixedKeyManager = string.IsNullOrWhiteSpace(req.FixedKeyManager) ? null : req.FixedKeyManager.Trim(),
                LocksmithBase = string.IsNullOrWhiteSpace(req.LocksmithBase)
                    ? "https://locksmith.unlock-protocol.com"
                    : req.LocksmithBase.Trim(),
                LocksmithToken = string.IsNullOrWhiteSpace(req.LocksmithToken) ? null : req.LocksmithToken.Trim(),
                MaxSupply = req.MaxSupply,
                Enabled = req.Enabled,
                RevokedAt = req.Enabled ? null : DateTimeOffset.UtcNow
            };

            await resolver.UpsertMappingAsync(entry, req.Enabled, ct);
            return Results.Json(MapToDto(entry));
        });

        group.MapDelete("/mappings/{chainId:long}/{seller}/{sku}", async (long chainId, string seller, string sku, IUnlockMappingResolver resolver, CancellationToken ct) =>
        {
            if (chainId <= 0) return Results.BadRequest(new { error = "chainId must be > 0" });
            if (string.IsNullOrWhiteSpace(seller) || string.IsNullOrWhiteSpace(sku))
                return Results.BadRequest(new { error = "seller and sku are required" });

            bool deleted = await resolver.DisableMappingAsync(chainId, seller, sku, ct);
            if (!deleted) return Results.NotFound(new { error = "mapping not found" });
            return Results.Json(new { ok = true });
        });
    }

    private static UnlockMappingDto MapToDto(UnlockMappingEntry row)
    {
        return new UnlockMappingDto
        {
            ChainId = row.ChainId,
            Seller = row.Seller,
            Sku = row.Sku,
            LockAddress = row.LockAddress,
            RpcUrl = row.RpcUrl,
            DurationSeconds = row.DurationSeconds,
            ExpirationUnix = row.ExpirationUnix,
            KeyManagerMode = row.KeyManagerMode,
            FixedKeyManager = row.FixedKeyManager,
            LocksmithBase = row.LocksmithBase,
            HasLocksmithToken = !string.IsNullOrWhiteSpace(row.LocksmithToken),
            HasServicePrivateKey = !string.IsNullOrWhiteSpace(row.ServicePrivateKey),
            MaxSupply = row.MaxSupply,
            Enabled = row.Enabled,
            RevokedAt = row.RevokedAt
        };
    }
}
