using Nethereum.Signer;
using Nethereum.Util;

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

        group.MapPut("/mappings", async (UnlockMappingUpsertRequest req, IUnlockMappingResolver resolver, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var log = loggerFactory.CreateLogger("UnlockAdminMappings");

            if (req.ChainId <= 0) return Results.BadRequest(new { error = "chainId must be > 0" });
            if (string.IsNullOrWhiteSpace(req.Seller) || string.IsNullOrWhiteSpace(req.Sku))
                return Results.BadRequest(new { error = "seller and sku are required" });
            if (string.IsNullOrWhiteSpace(req.ServicePrivateKey))
                return Results.BadRequest(new { error = "servicePrivateKey is required" });
            if (req.MaxSupply < 0)
                return Results.BadRequest(new { error = "maxSupply must be >= 0" });

            if (!IsValidAddress(req.Seller))
                return Results.BadRequest(new { error = "seller must be a valid EVM address" });
            if (!IsValidAddress(req.LockAddress))
                return Results.BadRequest(new { error = "lockAddress must be a valid EVM address" });
            if (!IsValidAbsoluteHttpUri(req.RpcUrl))
                return Results.BadRequest(new { error = "rpcUrl must be an absolute http or https URI" });
            if (!IsValidAbsoluteHttpUri(req.LocksmithBase))
                return Results.BadRequest(new { error = "locksmithBase must be an absolute http or https URI" });
            if (!TryDeriveAddress(req.ServicePrivateKey, out _, log))
                return Results.BadRequest(new { error = "servicePrivateKey is invalid or cannot derive an address" });

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
            if (keyManagerMode == "fixed" && !IsValidAddress(req.FixedKeyManager!))
                return Results.BadRequest(new { error = "fixedKeyManager must be a valid EVM address when keyManagerMode=fixed" });

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
            if (!IsValidAddress(seller))
                return Results.BadRequest(new { error = "seller must be a valid EVM address" });

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
            HasServicePrivateKey = !string.IsNullOrWhiteSpace(row.ServicePrivateKey),
            MaxSupply = row.MaxSupply,
            Enabled = row.Enabled,
            RevokedAt = row.RevokedAt
        };
    }

    private static bool IsValidAddress(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
               && AddressUtil.Current.IsValidEthereumAddressHexFormat(value.Trim());
    }

    private static bool IsValidAbsoluteHttpUri(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
    }

    private static bool TryDeriveAddress(string privateKey, out string address, ILogger log)
    {
        address = string.Empty;
        try
        {
            var key = new EthECKey(privateKey.Trim());
            address = key.GetPublicAddress();
            return !string.IsNullOrWhiteSpace(address);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Failed to derive address from provided servicePrivateKey during admin validation");
            return false;
        }
    }
}
