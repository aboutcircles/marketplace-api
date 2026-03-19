using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;
using Nethereum.Contracts.CQS;
using Nethereum.JsonRpc.Client;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Util;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;

namespace Circles.Market.Adapters.Unlock;

public sealed class UnlockMintedTicket
{
    public int TicketIndex { get; init; }
    public BigInteger KeyId { get; init; }
    public long ExpirationUnix { get; init; }
    public string? TransactionHash { get; init; }
    public JsonElement? Ticket { get; init; }
    public string? QrCodeDataUrl { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public string? Error { get; init; }
}

public sealed class UnlockMintOutcome
{
    public bool Success { get; init; }
    public string? TransactionHash { get; init; }
    public long ExpirationUnix { get; init; }
    public int RequestedQuantity { get; init; }
    public IReadOnlyList<UnlockMintedTicket> Tickets { get; init; } = Array.Empty<UnlockMintedTicket>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public string? Error { get; init; }
}

public interface IUnlockClient
{
    Task<UnlockMintOutcome> MintTicketsAsync(UnlockMappingEntry mapping, string buyerAddress, int quantity, CancellationToken ct);
    Task<string> GetTicketQrCodeDataUrlAsync(UnlockMappingEntry mapping, BigInteger keyId, CancellationToken ct);
    Task<(JsonElement? Ticket, string? QrCodeDataUrl, IReadOnlyList<string> Warnings)> GetOrGenerateQrCodeAsync(
        UnlockMappingEntry mapping,
        BigInteger keyId,
        CancellationToken ct);
}

public sealed class UnlockClient : IUnlockClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILocksmithAuthProvider _authProvider;
    private readonly ILogger<UnlockClient> _log;

    public UnlockClient(IHttpClientFactory httpClientFactory, ILocksmithAuthProvider authProvider, ILogger<UnlockClient> log)
    {
        _httpClientFactory = httpClientFactory;
        _authProvider = authProvider;
        _log = log;
    }

    public async Task<UnlockMintOutcome> MintTicketsAsync(UnlockMappingEntry mapping, string buyerAddress, int quantity, CancellationToken ct)
    {
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "quantity must be > 0");

        var expirationUnix = ResolveExpiration(mapping);
        UnlockPreflightInfo? preflight = null;

        try
        {
            var account = new Account(mapping.ServicePrivateKey.Trim(), mapping.ChainId);
            var web3 = new Web3(account, mapping.RpcUrl.Trim());

            var code = await web3.Eth.GetCode.SendRequestAsync(mapping.LockAddress.Trim());
            if (string.IsNullOrWhiteSpace(code) || code == "0x")
            {
                return new UnlockMintOutcome { Success = false, RequestedQuantity = quantity, ExpirationUnix = expirationUnix, Error = "No contract code found at configured lock address" };
            }

            preflight = await GetPreflightInfoAsync(web3, mapping.LockAddress.Trim(), account.Address, buyerAddress, ct);
            var activity = Activity.Current;
            activity?.SetTag("unlock.chain_id", mapping.ChainId);
            activity?.SetTag("unlock.lock_address", mapping.LockAddress);
            activity?.SetTag("unlock.buyer", buyerAddress);
            activity?.SetTag("unlock.quantity", quantity);
            activity?.SetTag("unlock.preflight.version", preflight.PublicLockVersion?.ToString() ?? "unknown");
            activity?.SetTag("unlock.preflight.is_lock_manager", BoolToText(preflight.IsLockManager));
            activity?.SetTag("unlock.preflight.has_key_granter_role", BoolToText(preflight.HasKeyGranterRole));
            activity?.SetTag("unlock.preflight.max_keys_per_address", preflight.MaxKeysPerAddress?.ToString() ?? "unknown");
            activity?.SetTag("unlock.preflight.buyer_total_keys", preflight.BuyerTotalKeys?.ToString() ?? "unknown");
            if (!string.IsNullOrWhiteSpace(preflight.PreflightError)) activity?.SetTag("unlock.preflight.error", preflight.PreflightError);

            if (preflight.MaxKeysPerAddress.HasValue && preflight.BuyerTotalKeys.HasValue &&
                preflight.BuyerTotalKeys.Value + quantity > preflight.MaxKeysPerAddress.Value)
            {
                return new UnlockMintOutcome
                {
                    Success = false,
                    RequestedQuantity = quantity,
                    ExpirationUnix = expirationUnix,
                    Error = $"Buyer {buyerAddress} would exceed maxKeysPerAddress={preflight.MaxKeysPerAddress.Value} (has {preflight.BuyerTotalKeys.Value}, requested {quantity})"
                };
            }

            if (preflight.CanGrant == false)
            {
                return new UnlockMintOutcome { Success = false, RequestedQuantity = quantity, ExpirationUnix = expirationUnix, Error = "Service signer is neither lock manager nor key granter" };
            }

            var keyManager = ResolveKeyManager(mapping, buyerAddress, account.Address);
            var handler = web3.Eth.GetContractTransactionHandler<GrantKeysFunction>();

            var receipt = await handler.SendRequestAndWaitForReceiptAsync(
                mapping.LockAddress.Trim(),
                new GrantKeysFunction
                {
                    Recipients = Enumerable.Repeat(buyerAddress, quantity).ToList(),
                    ExpirationTimestamps = Enumerable.Repeat(new BigInteger(expirationUnix), quantity).ToList(),
                    KeyManagers = Enumerable.Repeat(keyManager, quantity).ToList()
                },
                ct);

            if (receipt.Status?.Value != 1)
            {
                return new UnlockMintOutcome { Success = false, RequestedQuantity = quantity, ExpirationUnix = expirationUnix, TransactionHash = receipt.TransactionHash, Error = "grantKeys transaction failed" };
            }

            var keyIds = ExtractMintedTokenIdsFromTransfer(web3, receipt, mapping.LockAddress, buyerAddress);
            if (keyIds.Count != quantity)
            {
                return new UnlockMintOutcome
                {
                    Success = false,
                    RequestedQuantity = quantity,
                    ExpirationUnix = expirationUnix,
                    TransactionHash = receipt.TransactionHash,
                    Error = $"Minted key count mismatch: expected {quantity}, got {keyIds.Count}"
                };
            }

            var tickets = new List<UnlockMintedTicket>();
            var runWarnings = new List<string>();
            for (var i = 0; i < keyIds.Count; i++)
            {
                var keyId = keyIds[i];
                try
                {
                    var qrResult = await GetOrGenerateQrCodeAsync(mapping, keyId, ct);
                    tickets.Add(new UnlockMintedTicket
                    {
                        TicketIndex = i,
                        KeyId = keyId,
                        ExpirationUnix = expirationUnix,
                        TransactionHash = receipt.TransactionHash,
                        Ticket = qrResult.Ticket,
                        QrCodeDataUrl = qrResult.QrCodeDataUrl,
                        Warnings = qrResult.Warnings
                    });
                    runWarnings.AddRange(qrResult.Warnings);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex,
                        "Locksmith enrichment failed after successful mint. chain={Chain} seller={Seller} sku={Sku} keyId={KeyId}",
                        mapping.ChainId,
                        mapping.Seller,
                        mapping.Sku,
                        keyId);
                    Activity.Current?.AddEvent(new ActivityEvent("unlock.locksmith.enrichment_failed"));
                    Activity.Current?.SetTag("unlock.locksmith.enrichment_error", ex.Message);

                    const string warningCode = "locksmithEnrichmentFailed";
                    runWarnings.Add(warningCode);
                    tickets.Add(new UnlockMintedTicket
                    {
                        TicketIndex = i,
                        KeyId = keyId,
                        ExpirationUnix = expirationUnix,
                        TransactionHash = receipt.TransactionHash,
                        Warnings = new[] { warningCode },
                        Error = ex.Message
                    });
                }
            }

            return new UnlockMintOutcome
            {
                Success = true,
                RequestedQuantity = quantity,
                ExpirationUnix = expirationUnix,
                TransactionHash = receipt.TransactionHash,
                Tickets = tickets,
                Warnings = runWarnings.Distinct(StringComparer.Ordinal).ToList()
            };
        }
        catch (RpcResponseException ex)
        {
            _log.LogError(ex, "Unlock mint failed for chain={Chain} seller={Seller} sku={Sku} buyer={Buyer}", mapping.ChainId, mapping.Seller, mapping.Sku, buyerAddress);
            return new UnlockMintOutcome { Success = false, RequestedQuantity = quantity, ExpirationUnix = expirationUnix, Error = ex.RpcError?.Message ?? ex.Message };
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Unlock mint failed for chain={Chain} seller={Seller} sku={Sku} buyer={Buyer}", mapping.ChainId, mapping.Seller, mapping.Sku, buyerAddress);
            return new UnlockMintOutcome { Success = false, RequestedQuantity = quantity, ExpirationUnix = expirationUnix, Error = ex.Message };
        }
    }

    public async Task<string> GetTicketQrCodeDataUrlAsync(UnlockMappingEntry mapping, BigInteger keyId, CancellationToken ct)
    {
        var result = await GetOrGenerateQrCodeAsync(mapping, keyId, ct);
        if (!string.IsNullOrWhiteSpace(result.QrCodeDataUrl))
            return result.QrCodeDataUrl!;

        throw new HttpRequestException($"Locksmith QR code not available for keyId={keyId}", null, HttpStatusCode.BadGateway);
    }

    public async Task<(JsonElement? Ticket, string? QrCodeDataUrl, IReadOnlyList<string> Warnings)> GetOrGenerateQrCodeAsync(
        UnlockMappingEntry mapping,
        BigInteger keyId,
        CancellationToken ct)
    {
        var warnings = new List<string>();

        JsonElement? ticket = null;
        try
        {
            var ticketFetch = await TryFetchTicketWithRetryAsync(mapping, keyId, ct);
            ticket = ticketFetch.Ticket;
            if (!string.IsNullOrWhiteSpace(ticketFetch.Warning)) warnings.Add(ticketFetch.Warning!);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Locksmith ticket fetch failed, continuing with QR flow. chain={Chain} seller={Seller} sku={Sku} keyId={KeyId}",
                mapping.ChainId,
                mapping.Seller,
                mapping.Sku,
                keyId);
            Activity.Current?.AddEvent(new ActivityEvent("unlock.locksmith.ticket_fetch_failed"));
            Activity.Current?.SetTag("unlock.locksmith.ticket_fetch_error", ex.Message);
            warnings.Add("locksmithTicketFetchFailed");
        }

        var qrFetch = await TryFetchTicketQrCodeWithRetryAsync(mapping, keyId, ct);
        if (!string.IsNullOrWhiteSpace(qrFetch.Warning)) warnings.Add(qrFetch.Warning!);

        return (ticket, qrFetch.QrCodeDataUrl, warnings);
    }

    private static long ResolveExpiration(UnlockMappingEntry mapping)
    {
        if (mapping.ExpirationUnix.HasValue) return mapping.ExpirationUnix.Value;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long duration = mapping.DurationSeconds.GetValueOrDefault(0);
        if (duration <= 0) throw new InvalidOperationException("Invalid unlock mapping: durationSeconds must be > 0 when expirationUnix is not set");
        return checked(now + duration);
    }

    private static string ResolveKeyManager(UnlockMappingEntry mapping, string buyer, string service)
    {
        var mode = mapping.KeyManagerMode.Trim().ToLowerInvariant();
        return mode switch
        {
            "buyer" => buyer,
            "service" => service,
            "fixed" => string.IsNullOrWhiteSpace(mapping.FixedKeyManager)
                ? throw new InvalidOperationException("fixed key manager mode requires fixedKeyManager")
                : mapping.FixedKeyManager,
            _ => throw new InvalidOperationException($"Unsupported key manager mode: {mapping.KeyManagerMode}")
        };
    }

    private static List<BigInteger> ExtractMintedTokenIdsFromTransfer(Web3 web3, TransactionReceipt receipt, string expectedLockAddress, string expectedBuyer)
    {
        var transferEvent = web3.Eth.GetEvent<TransferEventDto>(expectedLockAddress);
        var decodedEvents = transferEvent.DecodeAllEventsForEvent(receipt.Logs);

        string normalizedLock = NormalizeAddress(expectedLockAddress);
        string normalizedBuyer = NormalizeAddress(expectedBuyer);
        string zeroAddress = NormalizeAddress("0x0000000000000000000000000000000000000000");

        return decodedEvents
            .Where(e => NormalizeAddress(e.Log.Address) == normalizedLock)
            .Where(e => NormalizeAddress(e.Event.From) == zeroAddress && NormalizeAddress(e.Event.To) == normalizedBuyer)
            .OrderBy(e => e.Log.LogIndex?.Value ?? long.MaxValue)
            .ThenBy(e => e.Log.TransactionIndex?.Value ?? long.MaxValue)
            .Select(e => e.Event.TokenId)
            .ToList();
    }

    private async Task<JsonElement?> FetchTicketAsync(UnlockMappingEntry mapping, BigInteger keyId, CancellationToken ct)
    {
        var relative = $"v2/api/ticket/{mapping.ChainId}/lock/{mapping.LockAddress}/key/{keyId}";
        var result = await GetLocksmithWithAuthRetryAsync(mapping, relative, ct);
        EnsureSuccess(result, "Locksmith ticket fetch failed");
        using var doc = JsonDocument.Parse(result.BodyText);
        return doc.RootElement.Clone();
    }

    private async Task<string> FetchTicketQrCodeDataUrlAsync(UnlockMappingEntry mapping, BigInteger keyId, CancellationToken ct)
    {
        var relative = $"v2/api/ticket/{mapping.ChainId}/lock/{mapping.LockAddress}/key/{keyId}/qr";
        var result = await GetLocksmithWithAuthRetryAsync(mapping, relative, ct);
        EnsureSuccess(result, "Locksmith QR fetch failed");
        if (result.BodyBytes.Length == 0) throw new HttpRequestException("Locksmith returned empty QR image payload", null, result.StatusCode);
        var base64 = Convert.ToBase64String(result.BodyBytes);
        return "data:image/png;base64," + base64;
    }

    private async Task GenerateTicketAsync(UnlockMappingEntry mapping, BigInteger keyId, CancellationToken ct)
    {
        var relative = $"v2/api/ticket/{mapping.ChainId}/lock/{mapping.LockAddress}/key/{keyId}/generate";
        var result = await GetLocksmithWithAuthRetryAsync(mapping, relative, ct);
        EnsureSuccess(result, "Locksmith generate failed");
    }

    private static void EnsureSuccess(LocksmithHttpResult result, string prefix)
    {
        if (!result.IsSuccessStatusCode)
            throw new HttpRequestException($"{prefix}: {(int)result.StatusCode} {result.ReasonPhrase}: {result.BodyText}", null, result.StatusCode);
    }

    private async Task<LocksmithHttpResult> GetLocksmithWithAuthRetryAsync(UnlockMappingEntry mapping, string relative, CancellationToken ct)
    {
        var first = await GetLocksmithAsync(mapping, relative, ct);
        if (first.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)) return first;

        _log.LogWarning("Locksmith request unauthorized, retrying after auth cache invalidate. chain={Chain} lock={Lock} path={Path}", mapping.ChainId, mapping.LockAddress, relative);
        await _authProvider.InvalidateAsync(mapping, ct);
        return await GetLocksmithAsync(mapping, relative, ct);
    }

    private async Task<LocksmithHttpResult> GetLocksmithAsync(UnlockMappingEntry mapping, string relative, CancellationToken ct)
    {
        using var client = _httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(mapping.LocksmithBase.Trim().TrimEnd('/') + "/");

        var token = await _authProvider.GetAccessTokenAsync(mapping, ct);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.GetAsync(relative, ct);
        var bodyBytes = await response.Content.ReadAsByteArrayAsync(ct);
        var bodyText = bodyBytes.Length == 0 ? string.Empty : Encoding.UTF8.GetString(bodyBytes);

        return new LocksmithHttpResult { StatusCode = response.StatusCode, ReasonPhrase = response.ReasonPhrase, BodyBytes = bodyBytes, BodyText = bodyText };
    }

    private async Task<(JsonElement? Ticket, string? Warning)> TryFetchTicketWithRetryAsync(UnlockMappingEntry mapping, BigInteger keyId, CancellationToken ct)
    {
        int timeoutMs = GetEnvInt("UNLOCK_LOCKSMITH_TICKET_FETCH_TIMEOUT_MS", 20000);
        int intervalMs = Math.Max(200, GetEnvInt("UNLOCK_LOCKSMITH_TICKET_FETCH_INTERVAL_MS", 1000));

        using var retryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        retryCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
        var retryCt = retryCts.Token;

        while (true)
        {
            try { return (await FetchTicketAsync(mapping, keyId, retryCt), null); }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                if (retryCt.IsCancellationRequested)
                    return (null, "locksmithTicketNotReady");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return (null, "locksmithTicketNotReady");
            }

            await Task.Delay(intervalMs, retryCt);
        }
    }

    private async Task<(string? QrCodeDataUrl, string? Warning)> TryFetchTicketQrCodeWithRetryAsync(UnlockMappingEntry mapping, BigInteger keyId, CancellationToken ct)
    {
        int timeoutMs = GetEnvInt("UNLOCK_LOCKSMITH_QR_FETCH_TIMEOUT_MS", 20000);
        int intervalMs = Math.Max(200, GetEnvInt("UNLOCK_LOCKSMITH_QR_FETCH_INTERVAL_MS", 1000));

        using var retryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        retryCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
        var retryCt = retryCts.Token;

        bool generateTriggered = false;
        while (true)
        {
            try { return (await FetchTicketQrCodeDataUrlAsync(mapping, keyId, retryCt), null); }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                if (!generateTriggered)
                {
                    generateTriggered = true;
                    try { await GenerateTicketAsync(mapping, keyId, retryCt); }
                    catch (Exception genEx)
                    {
                        _log.LogWarning(genEx, "Failed to trigger Locksmith ticket generation. chain={Chain} lock={Lock} keyId={KeyId}", mapping.ChainId, mapping.LockAddress, keyId);
                    }
                }

                if (retryCt.IsCancellationRequested)
                    return (null, "locksmithQrCodeNotReady");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return (null, "locksmithQrCodeNotReady");
            }

            await Task.Delay(intervalMs, retryCt);
        }
    }

    private static int GetEnvInt(string name, int defaultValue)
    {
        var val = Environment.GetEnvironmentVariable(name);
        return int.TryParse(val, out var parsed) && parsed > 0 ? parsed : defaultValue;
    }

    private static string NormalizeAddress(string address) => address.Trim().ToLowerInvariant();

    private static async Task<UnlockPreflightInfo> GetPreflightInfoAsync(Web3 web3, string lockAddress, string signer, string buyerAddress, CancellationToken ct)
    {
        var normalizedLock = NormalizeAddress(lockAddress);
        var normalizedSigner = NormalizeAddress(signer);
        var normalizedBuyer = NormalizeAddress(buyerAddress);

        var roleCurrent = "0x" + Sha3Keccack.Current.CalculateHash(Encoding.UTF8.GetBytes("KEY_GRANTER_ROLE"));
        var roleLegacy = "0x" + Sha3Keccack.Current.CalculateHash(Encoding.UTF8.GetBytes("KEY_GRANTER"));
        var preflightErrors = new List<string>();

        var version = await TryQueryAsync(() => web3.Eth.GetContractQueryHandler<PublicLockVersionFunction>().QueryAsync<ushort>(normalizedLock, new PublicLockVersionFunction(), null), "publicLockVersion", preflightErrors);
        var isManager = await TryQueryAsync(() => web3.Eth.GetContractQueryHandler<IsLockManagerFunction>().QueryAsync<bool>(normalizedLock, new IsLockManagerFunction { Account = normalizedSigner }, null), "isLockManager", preflightErrors);
        var hasRoleCurrent = await TryQueryAsync(() => web3.Eth.GetContractQueryHandler<HasRoleFunction>().QueryAsync<bool>(normalizedLock, new HasRoleFunction { Role = roleCurrent, Account = normalizedSigner }, null), "hasRole(KEY_GRANTER_ROLE)", preflightErrors);
        var hasRoleLegacy = await TryQueryAsync(() => web3.Eth.GetContractQueryHandler<HasRoleFunction>().QueryAsync<bool>(normalizedLock, new HasRoleFunction { Role = roleLegacy, Account = normalizedSigner }, null), "hasRole(KEY_GRANTER)", preflightErrors);
        var maxKeysPerAddress = await TryQueryAsync(() => web3.Eth.GetContractQueryHandler<MaxKeysPerAddressFunction>().QueryAsync<BigInteger>(normalizedLock, new MaxKeysPerAddressFunction(), null), "maxKeysPerAddress", preflightErrors);
        var buyerTotalKeys = await TryQueryAsync(() => web3.Eth.GetContractQueryHandler<TotalKeysFunction>().QueryAsync<BigInteger>(normalizedLock, new TotalKeysFunction { KeyOwner = normalizedBuyer }, null), "totalKeys(buyer)", preflightErrors);

        return new UnlockPreflightInfo
        {
            Signer = normalizedSigner,
            PublicLockVersion = version,
            IsLockManager = isManager,
            HasKeyGranterRole = hasRoleCurrent,
            HasKeyGranterRoleLegacy = hasRoleLegacy,
            MaxKeysPerAddress = maxKeysPerAddress,
            BuyerTotalKeys = buyerTotalKeys,
            PreflightError = preflightErrors.Count == 0 ? null : string.Join("; ", preflightErrors)
        };
    }

    private static async Task<T?> TryQueryAsync<T>(Func<Task<T>> query, string label, List<string> errors)
    {
        try { return await query(); }
        catch (Exception ex)
        {
            errors.Add($"{label}: {ex.GetType().Name} ({ex.Message})");
            return default;
        }
    }

    private static string BoolToText(bool? value) => value.HasValue ? (value.Value ? "true" : "false") : "unknown";

    private sealed class LocksmithHttpResult
    {
        public HttpStatusCode StatusCode { get; init; }
        public string? ReasonPhrase { get; init; }
        public byte[] BodyBytes { get; init; } = Array.Empty<byte>();
        public string BodyText { get; init; } = string.Empty;
        public bool IsSuccessStatusCode => (int)StatusCode is >= 200 and <= 299;
    }
}

internal sealed class UnlockPreflightInfo
{
    public string Signer { get; init; } = string.Empty;
    public ushort? PublicLockVersion { get; init; }
    public bool? IsLockManager { get; init; }
    public bool? HasKeyGranterRole { get; init; }
    public bool? HasKeyGranterRoleLegacy { get; init; }
    public BigInteger? MaxKeysPerAddress { get; init; }
    public BigInteger? BuyerTotalKeys { get; init; }
    public string? PreflightError { get; init; }
    public bool? CanGrant
    {
        get
        {
            if (IsLockManager == true || HasKeyGranterRole == true || HasKeyGranterRoleLegacy == true) return true;
            if (IsLockManager.HasValue && HasKeyGranterRole.HasValue && HasKeyGranterRoleLegacy.HasValue) return false;
            return null;
        }
    }
}

[Function("maxKeysPerAddress", "uint256")]
public sealed class MaxKeysPerAddressFunction : FunctionMessage { }

[Function("totalKeys", "uint256")]
public sealed class TotalKeysFunction : FunctionMessage
{
    [Parameter("address", "_keyOwner", 1)]
    public string KeyOwner { get; set; } = string.Empty;
}

[Function("publicLockVersion", "uint16")]
public sealed class PublicLockVersionFunction : FunctionMessage { }

[Function("isLockManager", "bool")]
public sealed class IsLockManagerFunction : FunctionMessage
{
    [Parameter("address", "account", 1)]
    public string Account { get; set; } = string.Empty;
}

[Function("hasRole", "bool")]
public sealed class HasRoleFunction : FunctionMessage
{
    [Parameter("bytes32", "role", 1)]
    public string Role { get; set; } = string.Empty;

    [Parameter("address", "account", 2)]
    public string Account { get; set; } = string.Empty;
}

[Function("grantKeys", "uint256[]")]
public sealed class GrantKeysFunction : FunctionMessage
{
    [Parameter("address[]", "_recipients", 1)]
    public List<string> Recipients { get; set; } = new();

    [Parameter("uint256[]", "_expirationTimestamps", 2)]
    public List<BigInteger> ExpirationTimestamps { get; set; } = new();

    [Parameter("address[]", "_keyManagers", 3)]
    public List<string> KeyManagers { get; set; } = new();
}

[Event("Transfer")]
public sealed class TransferEventDto : IEventDTO
{
    [Parameter("address", "from", 1, true)]
    public string From { get; set; } = string.Empty;

    [Parameter("address", "to", 2, true)]
    public string To { get; set; } = string.Empty;

    [Parameter("uint256", "tokenId", 3, true)]
    public BigInteger TokenId { get; set; }
}
