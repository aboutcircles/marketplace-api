using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;
using Nethereum.Contracts.CQS;
using Nethereum.JsonRpc.Client;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Signer;
using Nethereum.Util;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;

namespace Circles.Market.Adapters.Unlock;

public sealed class UnlockMintOutcome
{
    public bool Success { get; init; }
    public string? TransactionHash { get; init; }
    public BigInteger? KeyId { get; init; }
    public long ExpirationUnix { get; init; }
    public JsonElement? Ticket { get; init; }
    public string? QrCodeDataUrl { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public string? Error { get; init; }
}

public interface IUnlockClient
{
    Task<UnlockMintOutcome> MintTicketAsync(UnlockMappingEntry mapping, string buyerAddress, CancellationToken ct);
    Task<string> GetTicketQrCodeDataUrlAsync(UnlockMappingEntry mapping, BigInteger keyId, CancellationToken ct);
}

public sealed class UnlockClient : IUnlockClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<UnlockClient> _log;

    public UnlockClient(IHttpClientFactory httpClientFactory, ILogger<UnlockClient> log)
    {
        _httpClientFactory = httpClientFactory;
        _log = log;
    }

    public async Task<UnlockMintOutcome> MintTicketAsync(UnlockMappingEntry mapping, string buyerAddress, CancellationToken ct)
    {
        var expirationUnix = ResolveExpiration(mapping);
        UnlockPreflightInfo? preflight = null;

        try
        {
            var account = new Account(mapping.ServicePrivateKey.Trim(), mapping.ChainId);
            var web3 = new Web3(account, mapping.RpcUrl.Trim());

            var code = await web3.Eth.GetCode.SendRequestAsync(mapping.LockAddress.Trim());
            if (string.IsNullOrWhiteSpace(code) || code == "0x")
            {
                return new UnlockMintOutcome
                {
                    Success = false,
                    ExpirationUnix = expirationUnix,
                    Error = "No contract code found at configured lock address"
                };
            }

            preflight = await GetPreflightInfoAsync(web3, mapping.LockAddress.Trim(), account.Address, buyerAddress, ct);

            _log.LogInformation(
                "Unlock preflight: lock={Lock} version={Version} signer={Signer} isLockManager={IsLockManager} hasRole(KEY_GRANTER_ROLE)={HasRoleCurrent} hasRole(KEY_GRANTER)={HasRoleLegacy} onKeyGrantHook={OnKeyGrantHook} onHasRoleHook={OnHasRoleHook} onKeyPurchaseHook={OnKeyPurchaseHook} preflightError={PreflightError}",
                mapping.LockAddress,
                preflight.PublicLockVersion?.ToString() ?? "unknown",
                preflight.Signer,
                BoolToText(preflight.IsLockManager),
                BoolToText(preflight.HasKeyGranterRole),
                BoolToText(preflight.HasKeyGranterRoleLegacy),
                preflight.OnKeyGrantHook,
                preflight.OnHasRoleHook,
                preflight.OnKeyPurchaseHook,
                preflight.PreflightError ?? "none");

            if (preflight.MaxKeysPerAddress.HasValue && preflight.BuyerTotalKeys.HasValue &&
                preflight.BuyerTotalKeys.Value >= preflight.MaxKeysPerAddress.Value)
            {
                var maxKeysReason =
                    $"Buyer {buyerAddress} already has {preflight.BuyerTotalKeys.Value} keys, reaching lock maxKeysPerAddress={preflight.MaxKeysPerAddress.Value}. grantKeys would revert with MAX_KEYS_REACHED.";

                _log.LogWarning(
                    "Unlock preflight rejected grantKeys. {Reason} lockVersion={Version} signer={Signer}",
                    maxKeysReason,
                    preflight.PublicLockVersion?.ToString() ?? "unknown",
                    preflight.Signer);

                return new UnlockMintOutcome
                {
                    Success = false,
                    ExpirationUnix = expirationUnix,
                    Error = maxKeysReason
                };
            }

            if (preflight.CanGrant == false)
            {
                var authReason = $"Service signer {preflight.Signer} is neither lock manager nor key granter on lock {mapping.LockAddress} " +
                                 $"(isLockManager={BoolToText(preflight.IsLockManager)}, hasRole(KEY_GRANTER_ROLE)={BoolToText(preflight.HasKeyGranterRole)}, hasRole(KEY_GRANTER)={BoolToText(preflight.HasKeyGranterRoleLegacy)}).";

                _log.LogWarning(
                    "Unlock preflight rejected grantKeys. {Reason} lockVersion={Version} onKeyGrantHook={OnKeyGrantHook} onHasRoleHook={OnHasRoleHook} onKeyPurchaseHook={OnKeyPurchaseHook}",
                    authReason,
                    preflight.PublicLockVersion?.ToString() ?? "unknown",
                    preflight.OnKeyGrantHook,
                    preflight.OnHasRoleHook,
                    preflight.OnKeyPurchaseHook);

                return new UnlockMintOutcome
                {
                    Success = false,
                    ExpirationUnix = expirationUnix,
                    Error = authReason
                };
            }

            var keyManager = ResolveKeyManager(mapping, buyerAddress, account.Address);
            var handler = web3.Eth.GetContractTransactionHandler<GrantKeysFunction>();

            var receipt = await handler.SendRequestAndWaitForReceiptAsync(
                mapping.LockAddress.Trim(),
                new GrantKeysFunction
                {
                    Recipients = new List<string> { buyerAddress },
                    ExpirationTimestamps = new List<BigInteger> { new BigInteger(expirationUnix) },
                    KeyManagers = new List<string> { keyManager }
                },
                ct);

            if (receipt.Status?.Value != 1)
            {
                return new UnlockMintOutcome
                {
                    Success = false,
                    ExpirationUnix = expirationUnix,
                    TransactionHash = receipt.TransactionHash,
                    Error = "grantKeys transaction failed"
                };
            }

            var keyId = ExtractMintedTokenIdFromTransfer(web3, receipt, mapping.LockAddress, buyerAddress);
            if (!keyId.HasValue)
            {
                return new UnlockMintOutcome
                {
                    Success = false,
                    ExpirationUnix = expirationUnix,
                    TransactionHash = receipt.TransactionHash,
                    Error = "Minted keyId could not be derived from Transfer event"
                };
            }

            JsonElement? ticket = null;
            string? qrCodeDataUrl = null;
            var warnings = new List<string>();

            try
            {
                var ticketFetch = await TryFetchTicketWithRetryAsync(mapping, keyId.Value, ct);
                ticket = ticketFetch.Ticket;
                if (!string.IsNullOrWhiteSpace(ticketFetch.Warning))
                {
                    warnings.Add(ticketFetch.Warning);
                }
            }
            catch (Exception ex)
            {
                warnings.Add("locksmithFetchFailed");
                _log.LogWarning(ex,
                    "Locksmith fetch failed for chain={Chain} lock={Lock} keyId={KeyId}",
                    mapping.ChainId,
                    mapping.LockAddress,
                    keyId.Value);
            }

            try
            {
                var qrFetch = await TryFetchTicketQrCodeWithRetryAsync(mapping, keyId.Value, ct);
                qrCodeDataUrl = qrFetch.QrCodeDataUrl;
                if (!string.IsNullOrWhiteSpace(qrFetch.Warning))
                {
                    warnings.Add(qrFetch.Warning);
                }
            }
            catch (Exception ex)
            {
                warnings.Add("locksmithQrCodeFetchFailed");
                _log.LogWarning(ex,
                    "Locksmith QR code fetch failed for chain={Chain} lock={Lock} keyId={KeyId}",
                    mapping.ChainId,
                    mapping.LockAddress,
                    keyId.Value);
            }

            return new UnlockMintOutcome
            {
                Success = true,
                ExpirationUnix = expirationUnix,
                TransactionHash = receipt.TransactionHash,
                KeyId = keyId,
                Ticket = ticket,
                QrCodeDataUrl = qrCodeDataUrl,
                Warnings = warnings
            };
        }
        catch (RpcResponseException ex)
        {
            _log.LogError(ex,
                "Unlock mint failed for chain={Chain} seller={Seller} sku={Sku} buyer={Buyer}",
                mapping.ChainId,
                mapping.Seller,
                mapping.Sku,
                buyerAddress);

            var rpcMessage = ex.RpcError?.Message ?? ex.Message;
            var lower = rpcMessage.ToLowerInvariant();
            var likelyAuthIssue = lower.Contains("estimate") || lower.Contains("revert") || lower.Contains("execution reverted");
            var preflightSummary = preflight is null
                ? null
                : $"preflight(version={preflight.PublicLockVersion?.ToString() ?? "unknown"}, signer={preflight.Signer}, isLockManager={BoolToText(preflight.IsLockManager)}, hasRole(KEY_GRANTER_ROLE)={BoolToText(preflight.HasKeyGranterRole)}, hasRole(KEY_GRANTER)={BoolToText(preflight.HasKeyGranterRoleLegacy)}, buyerTotalKeys={preflight.BuyerTotalKeys?.ToString() ?? "unknown"}, maxKeysPerAddress={preflight.MaxKeysPerAddress?.ToString() ?? "unknown"}, onKeyGrantHook={preflight.OnKeyGrantHook}, onHasRoleHook={preflight.OnHasRoleHook}, onKeyPurchaseHook={preflight.OnKeyPurchaseHook}, preflightError={preflight.PreflightError ?? "none"})";

            var hint = likelyAuthIssue
                ? "Likely on-chain authorization/config issue. Ensure service wallet is lock manager or has KEY_GRANTER_ROLE, lockAddress/chainId/RPC match, and lock hooks/constraints allow this grant."
                : null;

            var error = string.Join(" | ", new[] { rpcMessage, hint, preflightSummary }.Where(s => !string.IsNullOrWhiteSpace(s))!);

            return new UnlockMintOutcome
            {
                Success = false,
                ExpirationUnix = expirationUnix,
                Error = error
            };
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Unlock mint failed for chain={Chain} seller={Seller} sku={Sku} buyer={Buyer}",
                mapping.ChainId,
                mapping.Seller,
                mapping.Sku,
                buyerAddress);

            return new UnlockMintOutcome
            {
                Success = false,
                ExpirationUnix = expirationUnix,
                Error = ex.Message
            };
        }
    }

    private static long ResolveExpiration(UnlockMappingEntry mapping)
    {
        if (mapping.ExpirationUnix.HasValue)
            return mapping.ExpirationUnix.Value;

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long duration = mapping.DurationSeconds.GetValueOrDefault(0);
        if (duration <= 0)
            throw new InvalidOperationException("Invalid unlock mapping: durationSeconds must be > 0 when expirationUnix is not set");

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

    private static BigInteger? ExtractMintedTokenIdFromTransfer(
        Web3 web3,
        TransactionReceipt receipt,
        string expectedLockAddress,
        string expectedBuyer)
    {
        var transferEvent = web3.Eth.GetEvent<TransferEventDto>(expectedLockAddress);
        var decodedEvents = transferEvent.DecodeAllEventsForEvent(receipt.Logs);

        string normalizedLock = NormalizeAddress(expectedLockAddress);
        string normalizedBuyer = NormalizeAddress(expectedBuyer);
        string zeroAddress = NormalizeAddress("0x0000000000000000000000000000000000000000");

        var mintEvent = decodedEvents
            .Where(e => NormalizeAddress(e.Log.Address) == normalizedLock)
            .Select(e => e.Event)
            .FirstOrDefault(e => NormalizeAddress(e.From) == zeroAddress && NormalizeAddress(e.To) == normalizedBuyer);

        return mintEvent?.TokenId;
    }

    private async Task<JsonElement?> FetchTicketAsync(UnlockMappingEntry mapping, BigInteger keyId, CancellationToken ct)
    {
        var relative = $"v2/api/ticket/{mapping.ChainId}/lock/{mapping.LockAddress}/key/{keyId}";

        var result = await GetLocksmithAsync(mapping, relative, ct);
        if (!result.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Locksmith returned {(int)result.StatusCode} {result.ReasonPhrase}: {result.BodyText}",
                inner: null,
                statusCode: result.StatusCode);
        }

        using var doc = JsonDocument.Parse(result.BodyText);
        return doc.RootElement.Clone();
    }

    public async Task<string> GetTicketQrCodeDataUrlAsync(UnlockMappingEntry mapping, BigInteger keyId, CancellationToken ct)
    {
        return await FetchTicketQrCodeDataUrlAsync(mapping, keyId, ct);
    }

    private async Task<string> FetchTicketQrCodeDataUrlAsync(UnlockMappingEntry mapping, BigInteger keyId, CancellationToken ct)
    {
        var relative = $"v2/api/ticket/{mapping.ChainId}/{mapping.LockAddress}/{keyId}/qr";
        var result = await GetLocksmithAsync(mapping, relative, ct);

        if (!result.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Locksmith returned {(int)result.StatusCode} {result.ReasonPhrase}: {result.BodyText}",
                inner: null,
                statusCode: result.StatusCode);
        }

        if (result.BodyBytes.Length == 0)
        {
            throw new HttpRequestException(
                "Locksmith returned an empty QR image payload",
                inner: null,
                statusCode: result.StatusCode);
        }

        var base64 = Convert.ToBase64String(result.BodyBytes);
        return "data:image/png;base64," + base64;
    }

    private async Task GenerateTicketAsync(UnlockMappingEntry mapping, BigInteger keyId, CancellationToken ct)
    {
        var relative = $"v2/api/ticket/{mapping.ChainId}/lock/{mapping.LockAddress}/key/{keyId}/generate";

        var result = await GetLocksmithAsync(mapping, relative, ct);
        if (!result.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Locksmith generate returned {(int)result.StatusCode} {result.ReasonPhrase}: {result.BodyText}",
                inner: null,
                statusCode: result.StatusCode);
        }
    }

    private async Task<LocksmithHttpResult> GetLocksmithAsync(
        UnlockMappingEntry mapping,
        string relative,
        CancellationToken ct)
    {
        var firstAttempt = await GetLocksmithCoreAsync(mapping, relative, preferConfiguredToken: true, ct);

        bool shouldRetryWithSiwe =
            (firstAttempt.StatusCode == HttpStatusCode.Unauthorized || firstAttempt.StatusCode == HttpStatusCode.Forbidden)
            && CanLoginToLocksmith(mapping);

        if (!shouldRetryWithSiwe)
        {
            return firstAttempt;
        }

        _log.LogInformation(
            "Locksmith request was rejected, retrying with SIWE login. chain={Chain} lock={Lock} path={Path}",
            mapping.ChainId,
            mapping.LockAddress,
            relative);

        return await GetLocksmithCoreAsync(mapping, relative, preferConfiguredToken: false, ct);
    }

    private async Task<LocksmithHttpResult> GetLocksmithCoreAsync(
        UnlockMappingEntry mapping,
        string relative,
        bool preferConfiguredToken,
        CancellationToken ct)
    {
        using var client = _httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(mapping.LocksmithBase.Trim().TrimEnd('/') + "/");

        var bearerToken = await ResolveLocksmithBearerTokenAsync(mapping, preferConfiguredToken, ct);
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        using var response = await client.GetAsync(relative, ct);
        var bodyBytes = await response.Content.ReadAsByteArrayAsync(ct);
        var bodyText = TryDecodeBody(bodyBytes);

        return new LocksmithHttpResult
        {
            StatusCode = response.StatusCode,
            ReasonPhrase = response.ReasonPhrase,
            BodyBytes = bodyBytes,
            BodyText = bodyText
        };
    }

    private async Task<string?> ResolveLocksmithBearerTokenAsync(
        UnlockMappingEntry mapping,
        bool preferConfiguredToken,
        CancellationToken ct)
    {
        bool hasConfiguredToken = !string.IsNullOrWhiteSpace(mapping.LocksmithToken);
        if (preferConfiguredToken && hasConfiguredToken)
        {
            return mapping.LocksmithToken!.Trim();
        }

        if (CanLoginToLocksmith(mapping))
        {
            return await LoginToLocksmithAsync(mapping, ct);
        }

        if (!preferConfiguredToken && hasConfiguredToken)
        {
            return mapping.LocksmithToken!.Trim();
        }

        return null;
    }

    private static bool CanLoginToLocksmith(UnlockMappingEntry mapping)
    {
        return !string.IsNullOrWhiteSpace(mapping.ServicePrivateKey);
    }

    private async Task<string> LoginToLocksmithAsync(UnlockMappingEntry mapping, CancellationToken ct)
    {
        using var client = _httpClientFactory.CreateClient();
        var baseUri = new Uri(mapping.LocksmithBase.Trim().TrimEnd('/') + "/");
        client.BaseAddress = baseUri;

        var servicePrivateKey = mapping.ServicePrivateKey.Trim();
        var account = new Account(servicePrivateKey, mapping.ChainId);
        var nonce = await GetLocksmithNonceAsync(client, ct);
        var message = BuildSiweMessage(baseUri, account.Address, nonce);
        var signature = SignSiweMessage(servicePrivateKey, message);

        using var response = await client.PostAsJsonAsync("v2/auth/login", new LocksmithLoginRequest
        {
            Message = message,
            Signature = signature
        }, ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Locksmith login returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}",
                inner: null,
                statusCode: response.StatusCode);
        }

        var login = JsonSerializer.Deserialize<LocksmithLoginResponse>(body, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (login is null || string.IsNullOrWhiteSpace(login.AccessToken))
        {
            throw new InvalidOperationException("Locksmith login succeeded but did not return an access token.");
        }

        return login.AccessToken.Trim();
    }

    private static async Task<string> GetLocksmithNonceAsync(HttpClient client, CancellationToken ct)
    {
        using var response = await client.GetAsync("v2/auth/nonce", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Locksmith nonce returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}",
                inner: null,
                statusCode: response.StatusCode);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            throw new InvalidOperationException("Locksmith nonce response was empty.");
        }

        try
        {
            using var doc = JsonDocument.Parse(body);

            if (doc.RootElement.ValueKind == JsonValueKind.String)
            {
                var value = doc.RootElement.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("nonce", out var nonceProp) &&
                nonceProp.ValueKind == JsonValueKind.String)
            {
                var value = nonceProp.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }
        }
        catch (JsonException)
        {
            // fallback below
        }

        return body.Trim().Trim('"');
    }

    private static string BuildSiweMessage(Uri locksmithBaseUri, string address, string nonce)
    {
        var domain = locksmithBaseUri.Host;
        var uri = locksmithBaseUri.GetLeftPart(UriPartial.Authority);
        var issuedAt = DateTimeOffset.UtcNow.ToString("O");

        return
            $"{domain} wants you to sign in with your Ethereum account:\n" +
            $"{address}\n\n" +
            "Sign in with Ethereum to Unlock Protocol.\n\n" +
            $"URI: {uri}\n" +
            "Version: 1\n" +
            "Chain ID: 1\n" +
            $"Nonce: {nonce}\n" +
            $"Issued At: {issuedAt}";
    }

    private static string SignSiweMessage(string privateKey, string message)
    {
        var key = new EthECKey(privateKey);
        var signer = new EthereumMessageSigner();
        return signer.EncodeUTF8AndSign(message, key);
    }

    private static string TryDecodeBody(byte[] bodyBytes)
    {
        if (bodyBytes.Length == 0)
        {
            return string.Empty;
        }

        return Encoding.UTF8.GetString(bodyBytes);
    }

    private async Task<(JsonElement? Ticket, string? Warning)> TryFetchTicketWithRetryAsync(
        UnlockMappingEntry mapping,
        BigInteger keyId,
        CancellationToken ct)
    {
        int timeoutMs = GetEnvInt("UNLOCK_LOCKSMITH_TICKET_FETCH_TIMEOUT_MS", 20000);
        int intervalMs = Math.Max(200, GetEnvInt("UNLOCK_LOCKSMITH_TICKET_FETCH_INTERVAL_MS", 1000));

        using var retryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        retryCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
        var retryCt = retryCts.Token;

        int attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                var ticket = await FetchTicketAsync(mapping, keyId, retryCt);
                if (attempt > 1)
                {
                    _log.LogInformation(
                        "Locksmith ticket became available after retries. chain={Chain} lock={Lock} keyId={KeyId} attempts={Attempts}",
                        mapping.ChainId,
                        mapping.LockAddress,
                        keyId,
                        attempt);
                }
                return (ticket, null);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                if (retryCt.IsCancellationRequested)
                {
                    if (ct.IsCancellationRequested)
                        ct.ThrowIfCancellationRequested();

                    _log.LogInformation(
                        "Locksmith ticket not yet available after retry window. chain={Chain} lock={Lock} keyId={KeyId} attempts={Attempts}",
                        mapping.ChainId,
                        mapping.LockAddress,
                        keyId,
                        attempt);
                    return (null, "locksmithTicketNotReady");
                }

                _log.LogDebug(
                    "Locksmith ticket still not found, will retry. chain={Chain} lock={Lock} keyId={KeyId} attempt={Attempt}",
                    mapping.ChainId,
                    mapping.LockAddress,
                    keyId,
                    attempt);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _log.LogInformation(
                    "Locksmith ticket fetch timed out. chain={Chain} lock={Lock} keyId={KeyId} attempts={Attempts}",
                    mapping.ChainId,
                    mapping.LockAddress,
                    keyId,
                    attempt);
                return (null, "locksmithTicketNotReady");
            }

            await Task.Delay(intervalMs, retryCt);
        }
    }

    private async Task<(string? QrCodeDataUrl, string? Warning)> TryFetchTicketQrCodeWithRetryAsync(
        UnlockMappingEntry mapping,
        BigInteger keyId,
        CancellationToken ct)
    {
        int timeoutMs = GetEnvInt("UNLOCK_LOCKSMITH_QR_FETCH_TIMEOUT_MS", 20000);
        int intervalMs = Math.Max(200, GetEnvInt("UNLOCK_LOCKSMITH_QR_FETCH_INTERVAL_MS", 1000));

        using var retryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        retryCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
        var retryCt = retryCts.Token;

        int attempt = 0;
        bool generateTriggered = false;
        while (true)
        {
            attempt++;
            try
            {
                var qrCodeDataUrl = await FetchTicketQrCodeDataUrlAsync(mapping, keyId, retryCt);
                if (attempt > 1)
                {
                    _log.LogInformation(
                        "Locksmith QR code became available after retries. chain={Chain} lock={Lock} keyId={KeyId} attempts={Attempts}",
                        mapping.ChainId,
                        mapping.LockAddress,
                        keyId,
                        attempt);
                }

                return (qrCodeDataUrl, null);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                if (!generateTriggered)
                {
                    generateTriggered = true;

                    try
                    {
                        await GenerateTicketAsync(mapping, keyId, retryCt);
                        _log.LogInformation(
                            "Triggered Locksmith ticket generation before QR retry. chain={Chain} lock={Lock} keyId={KeyId}",
                            mapping.ChainId,
                            mapping.LockAddress,
                            keyId);
                    }
                    catch (Exception generateEx)
                    {
                        _log.LogWarning(generateEx,
                            "Failed to trigger Locksmith ticket generation. chain={Chain} lock={Lock} keyId={KeyId}",
                            mapping.ChainId,
                            mapping.LockAddress,
                            keyId);
                    }
                }

                if (retryCt.IsCancellationRequested)
                {
                    if (ct.IsCancellationRequested)
                        ct.ThrowIfCancellationRequested();

                    _log.LogInformation(
                        "Locksmith QR code not yet available after retry window. chain={Chain} lock={Lock} keyId={KeyId} attempts={Attempts}",
                        mapping.ChainId,
                        mapping.LockAddress,
                        keyId,
                        attempt);
                    return (null, "locksmithQrCodeNotReady");
                }

                _log.LogDebug(
                    "Locksmith QR code still not found, will retry. chain={Chain} lock={Lock} keyId={KeyId} attempt={Attempt}",
                    mapping.ChainId,
                    mapping.LockAddress,
                    keyId,
                    attempt);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _log.LogInformation(
                    "Locksmith QR code fetch timed out. chain={Chain} lock={Lock} keyId={KeyId} attempts={Attempts}",
                    mapping.ChainId,
                    mapping.LockAddress,
                    keyId,
                    attempt);
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

    private static string NormalizeAddress(string address)
    {
        return address.Trim().ToLowerInvariant();
    }

    private static async Task<UnlockPreflightInfo> GetPreflightInfoAsync(Web3 web3, string lockAddress, string signer, string buyerAddress, CancellationToken ct)
    {
        var normalizedLock = NormalizeAddress(lockAddress);
        var normalizedSigner = NormalizeAddress(signer);
        var normalizedBuyer = NormalizeAddress(buyerAddress);

        var roleCurrent = "0x" + Sha3Keccack.Current.CalculateHash(Encoding.UTF8.GetBytes("KEY_GRANTER_ROLE"));
        var roleLegacy = "0x" + Sha3Keccack.Current.CalculateHash(Encoding.UTF8.GetBytes("KEY_GRANTER"));

        var preflightErrors = new List<string>();

        var version = await TryQueryAsync<ushort>(
            () => web3.Eth.GetContractQueryHandler<PublicLockVersionFunction>()
                .QueryAsync<ushort>(normalizedLock, new PublicLockVersionFunction(), null),
            "publicLockVersion",
            preflightErrors);

        var isManager = await TryQueryAsync<bool>(
            () => web3.Eth.GetContractQueryHandler<IsLockManagerFunction>()
                .QueryAsync<bool>(normalizedLock, new IsLockManagerFunction { Account = normalizedSigner }, null),
            "isLockManager",
            preflightErrors);

        var hasRoleCurrent = await TryQueryAsync<bool>(
            () => web3.Eth.GetContractQueryHandler<HasRoleFunction>()
                .QueryAsync<bool>(normalizedLock, new HasRoleFunction { Role = roleCurrent, Account = normalizedSigner }, null),
            "hasRole(KEY_GRANTER_ROLE)",
            preflightErrors);

        var hasRoleLegacy = await TryQueryAsync<bool>(
            () => web3.Eth.GetContractQueryHandler<HasRoleFunction>()
                .QueryAsync<bool>(normalizedLock, new HasRoleFunction { Role = roleLegacy, Account = normalizedSigner }, null),
            "hasRole(KEY_GRANTER)",
            preflightErrors);

        var onKeyGrantHook = await TryQueryAsync<string>(
            () => web3.Eth.GetContractQueryHandler<OnKeyGrantHookFunction>()
                .QueryAsync<string>(normalizedLock, new OnKeyGrantHookFunction(), null),
            "onKeyGrantHook",
            preflightErrors);

        var onHasRoleHook = await TryQueryAsync<string>(
            () => web3.Eth.GetContractQueryHandler<OnHasRoleHookFunction>()
                .QueryAsync<string>(normalizedLock, new OnHasRoleHookFunction(), null),
            "onHasRoleHook",
            preflightErrors);

        var onKeyPurchaseHook = await TryQueryAsync<string>(
            () => web3.Eth.GetContractQueryHandler<OnKeyPurchaseHookFunction>()
                .QueryAsync<string>(normalizedLock, new OnKeyPurchaseHookFunction(), null),
            "onKeyPurchaseHook",
            preflightErrors);

        var maxKeysPerAddress = await TryQueryAsync<BigInteger>(
            () => web3.Eth.GetContractQueryHandler<MaxKeysPerAddressFunction>()
                .QueryAsync<BigInteger>(normalizedLock, new MaxKeysPerAddressFunction(), null),
            "maxKeysPerAddress",
            preflightErrors);

        var buyerTotalKeys = await TryQueryAsync<BigInteger>(
            () => web3.Eth.GetContractQueryHandler<TotalKeysFunction>()
                .QueryAsync<BigInteger>(normalizedLock, new TotalKeysFunction { KeyOwner = normalizedBuyer }, null),
            "totalKeys(buyer)",
            preflightErrors);

        return new UnlockPreflightInfo
        {
            Signer = normalizedSigner,
            PublicLockVersion = version,
            IsLockManager = isManager,
            HasKeyGranterRole = hasRoleCurrent,
            HasKeyGranterRoleLegacy = hasRoleLegacy,
            MaxKeysPerAddress = maxKeysPerAddress,
            BuyerTotalKeys = buyerTotalKeys,
            OnKeyGrantHook = NormalizeHook(onKeyGrantHook),
            OnHasRoleHook = NormalizeHook(onHasRoleHook),
            OnKeyPurchaseHook = NormalizeHook(onKeyPurchaseHook),
            PreflightError = preflightErrors.Count == 0 ? null : string.Join("; ", preflightErrors)
        };
    }

    private static async Task<T?> TryQueryAsync<T>(Func<Task<T>> query, string label, List<string> errors)
    {
        try
        {
            return await query();
        }
        catch (Exception ex)
        {
            errors.Add($"{label}: {ex.GetType().Name} ({ex.Message})");
            return default;
        }
    }

    private static string NormalizeHook(string? hook)
    {
        if (string.IsNullOrWhiteSpace(hook)) return "unknown";
        var normalized = NormalizeAddress(hook);
        return normalized == "0x0000000000000000000000000000000000000000" ? "none" : normalized;
    }

    private static string BoolToText(bool? value)
    {
        return value.HasValue ? (value.Value ? "true" : "false") : "unknown";
    }

    private sealed class LocksmithHttpResult
    {
        public HttpStatusCode StatusCode { get; init; }
        public string? ReasonPhrase { get; init; }
        public byte[] BodyBytes { get; init; } = Array.Empty<byte>();
        public string BodyText { get; init; } = string.Empty;
        public bool IsSuccessStatusCode => (int)StatusCode is >= 200 and <= 299;
    }

    private sealed class LocksmithLoginRequest
    {
        public string Message { get; init; } = string.Empty;
        public string Signature { get; init; } = string.Empty;
    }

    private sealed class LocksmithLoginResponse
    {
        public string AccessToken { get; init; } = string.Empty;
        public string? RefreshToken { get; init; }
        public string? WalletAddress { get; init; }
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
    public string OnKeyGrantHook { get; init; } = "unknown";
    public string OnHasRoleHook { get; init; } = "unknown";
    public string OnKeyPurchaseHook { get; init; } = "unknown";
    public string? PreflightError { get; init; }
    public bool? CanGrant
    {
        get
        {
            if (IsLockManager == true || HasKeyGranterRole == true || HasKeyGranterRoleLegacy == true)
            {
                return true;
            }

            if (IsLockManager.HasValue && HasKeyGranterRole.HasValue && HasKeyGranterRoleLegacy.HasValue)
            {
                return false;
            }

            return null;
        }
    }
}

[Function("maxKeysPerAddress", "uint256")]
public sealed class MaxKeysPerAddressFunction : FunctionMessage
{
}

[Function("totalKeys", "uint256")]
public sealed class TotalKeysFunction : FunctionMessage
{
    [Parameter("address", "_keyOwner", 1)]
    public string KeyOwner { get; set; } = string.Empty;
}

[Function("publicLockVersion", "uint16")]
public sealed class PublicLockVersionFunction : FunctionMessage
{
}

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

[Function("onKeyGrantHook", "address")]
public sealed class OnKeyGrantHookFunction : FunctionMessage
{
}

[Function("onHasRoleHook", "address")]
public sealed class OnHasRoleHookFunction : FunctionMessage
{
}

[Function("onKeyPurchaseHook", "address")]
public sealed class OnKeyPurchaseHookFunction : FunctionMessage
{
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
