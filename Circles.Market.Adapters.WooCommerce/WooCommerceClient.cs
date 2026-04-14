using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Circles.Market.Adapters.WooCommerce;

/// <summary>
/// Client for the WooCommerce REST API v3 using HTTP Basic Auth (consumer_key:consumer_secret).
/// Not registered as a singleton — constructed per-request so each seller can have different credentials.
/// </summary>
public sealed class WooCommerceClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly WooCommerceSettings _settings;
    private readonly ILogger<WooCommerceClient> _log;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null, // WooCommerce uses snake_case JSON
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public WooCommerceClient(HttpClient http, WooCommerceSettings settings, ILogger<WooCommerceClient> log)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        // Configure per-request timeout
        _http.Timeout = TimeSpan.FromMilliseconds(_settings.TimeoutMs);

        // Set BaseAddress once
        _http.BaseAddress = new Uri(_settings.ApiBaseUrl);

        // Set Basic Auth header (reused for all requests)
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_settings.ConsumerKey}:{_settings.ConsumerSecret}"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>Looks up a WooCommerce product by its SKU.</summary>
    /// <returns>The product if found, null otherwise.</returns>
    public async Task<WcProductDto?> GetProductBySkuAsync(string sku, CancellationToken ct)
    {
        try
        {
            var response = await _http.GetAsync($"products?sku={Uri.EscapeDataString(sku)}", ct);
            return await ParseProductResponseAsync(response, ct);
        }
        catch (TaskCanceledException) when (_http.Timeout != Timeout.InfiniteTimeSpan)
        {
            _log.LogError("Timeout fetching product by SKU={Sku} from {BaseUrl}", sku, _settings.BaseUrl);
            throw new WooCommerceApiException("wc_api_timeout", $"Request timed out after {_settings.TimeoutMs}ms");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error fetching product by SKU={Sku}", sku);
            throw new WooCommerceApiException("wc_api_request_failed", ex.Message);
        }
    }

    /// <summary>Fetches a WooCommerce product by its numeric ID.</summary>
    public async Task<WcProductDto?> GetProductByIdAsync(int productId, CancellationToken ct)
    {
        try
        {
            var response = await _http.GetAsync($"products/{productId}", ct);
            if (!response.IsSuccessStatusCode)
            {
                var (code, msg) = await ReadErrorAsync(response, ct);
                if (response.StatusCode == HttpStatusCode.NotFound || code == "woocommerce_rest_product_invalid_id")
                {
                    return null;
                }
                _log.LogError("WooCommerce GET /products/{Id} returned {Status}: {Code} {Message}", productId, response.StatusCode, code, msg);
                throw new WooCommerceApiException(code, msg);
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<WcProductDto>(json, JsonOptions);
        }
        catch (TaskCanceledException) when (_http.Timeout != Timeout.InfiniteTimeSpan)
        {
            _log.LogError("Timeout fetching product {Id}", productId);
            throw new WooCommerceApiException("wc_api_timeout", $"Request timed out after {_settings.TimeoutMs}ms");
        }
        catch (WooCommerceApiException) { throw; }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error fetching product {Id}", productId);
            throw new WooCommerceApiException("wc_api_request_failed", ex.Message);
        }
    }

    /// <summary>Lists products with optional SKU filter (admin proxy endpoint).</summary>
    public async Task<List<WcProductDto>> ListProductsAsync(string? sku = null, int perPage = 100, CancellationToken ct = default)
    {
        try
        {
            var url = $"products?per_page={perPage}";
            if (!string.IsNullOrWhiteSpace(sku))
            {
                url += $"&sku={Uri.EscapeDataString(sku)}";
            }

            var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                var (code, msg) = await ReadErrorAsync(response, ct);
                throw new WooCommerceApiException(code, msg);
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<List<WcProductDto>>(json, JsonOptions) ?? new();
        }
        catch (TaskCanceledException) when (_http.Timeout != Timeout.InfiniteTimeSpan)
        {
            throw new WooCommerceApiException("wc_api_timeout", "Request timed out");
        }
        catch (WooCommerceApiException) { throw; }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error listing products");
            throw new WooCommerceApiException("wc_api_request_failed", ex.Message);
        }
    }

    /// <summary>
    /// Creates a WooCommerce order. Returns the created order on success.
    /// </summary>
    public async Task<WcOrderDto> CreateOrderAsync(WcCreateOrderRequest request, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(request, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _http.PostAsync("orders", content, ct);

            if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
            {
                // WC returns 422 for validation errors (e.g. missing required fields)
                var (code, msg) = await ReadErrorAsync(response, ct);
                _log.LogWarning("WooCommerce order creation failed validation: {Code} {Message}", code, msg);
                throw new WooCommerceApiException(code, msg, isValidationError: true);
            }

            if (!response.IsSuccessStatusCode)
            {
                var (code, msg) = await ReadErrorAsync(response, ct);
                _log.LogError("WooCommerce POST /orders failed: {Status} {Code} {Message}", response.StatusCode, code, msg);
                throw new WooCommerceApiException(code, msg);
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            var order = JsonSerializer.Deserialize<WcOrderDto>(body, JsonOptions);
            if (order == null)
            {
                _log.LogError("WooCommerce returned null order body");
                throw new WooCommerceApiException("wc_api_null_response", "CreateOrder returned null");
            }

            _log.LogInformation("Created WooCommerce order {Id} (#{Number}) status={Status}",
                order.Id, order.Number, order.Status);
            return order;
        }
        catch (TaskCanceledException) when (_http.Timeout != Timeout.InfiniteTimeSpan)
        {
            _log.LogError("Timeout creating WooCommerce order");
            throw new WooCommerceApiException("wc_api_timeout", "Order creation timed out");
        }
        catch (WooCommerceApiException) { throw; }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error creating WooCommerce order");
            throw new WooCommerceApiException("wc_api_request_failed", ex.Message);
        }
    }

    /// <summary>Fetches an existing WooCommerce order by ID.</summary>
    public async Task<WcOrderDto?> GetOrderAsync(int orderId, CancellationToken ct)
    {
        try
        {
            var response = await _http.GetAsync($"orders/{orderId}", ct);
            if (!response.IsSuccessStatusCode)
            {
                var (code, msg) = await ReadErrorAsync(response, ct);
                throw new WooCommerceApiException(code, msg);
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<WcOrderDto>(json, JsonOptions);
        }
        catch (TaskCanceledException) when (_http.Timeout != Timeout.InfiniteTimeSpan)
        {
            throw new WooCommerceApiException("wc_api_timeout", "Request timed out");
        }
        catch (WooCommerceApiException) { throw; }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error fetching order {Id}", orderId);
            throw new WooCommerceApiException("wc_api_request_failed", ex.Message);
        }
    }

    /// <summary>
    /// Looks up or creates a WooCommerce customer by email.
    /// Returns the WC customer ID.
    /// </summary>
    public async Task<int> GetOrCreateCustomerAsync(string email, string? firstName, string? lastName,
        WcAddressDto? billing, WcAddressDto? shipping, CancellationToken ct)
    {
        try
        {
            // Try to find existing customer by email
            var searchResponse = await _http.GetAsync(
                $"customers?email={Uri.EscapeDataString(email)}&per_page=1", ct);

            if (searchResponse.IsSuccessStatusCode)
            {
                var body = await searchResponse.Content.ReadAsStringAsync(ct);
                var customers = JsonSerializer.Deserialize<List<WcCustomerDto>>(body, JsonOptions);
                if (customers is { Count: > 0 })
                {
                    _log.LogInformation("Found existing WC customer {Id} for email {Email}", customers[0].Id, email);
                    return customers[0].Id;
                }
            }

            // Create new customer
            var createRequest = new WcCustomerCreateRequest
            {
                Email = email,
                FirstName = firstName,
                LastName = lastName,
                Billing = billing,
                Shipping = shipping
            };

            var json = JsonSerializer.Serialize(createRequest, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _http.PostAsync("customers", content, ct);
            if (!response.IsSuccessStatusCode)
            {
                var (code, msg) = await ReadErrorAsync(response, ct);
                _log.LogWarning("Failed to create WC customer: {Code} {Message}. Using default.", code, msg);
                // Fall back to default customer if configured
                if (_settings.DefaultCustomerId.HasValue)
                {
                    return _settings.DefaultCustomerId.Value;
                }
                throw new WooCommerceApiException(code, msg);
            }

            var createdBody = await response.Content.ReadAsStringAsync(ct);
            var created = JsonSerializer.Deserialize<WcCustomerDto>(createdBody, JsonOptions);
            _log.LogInformation("Created new WC customer {Id} for email {Email}", created?.Id, email);
            return created?.Id ?? 0;
        }
        catch (TaskCanceledException) when (_http.Timeout != Timeout.InfiniteTimeSpan)
        {
            throw new WooCommerceApiException("wc_api_timeout", "Customer lookup/creation timed out");
        }
        catch (WooCommerceApiException) { throw; }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error in GetOrCreateCustomer for {Email}", email);
            throw new WooCommerceApiException("wc_api_request_failed", ex.Message);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<WcProductDto?> ParseProductResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var (code, msg) = await ReadErrorAsync(response, ct);
            if (response.StatusCode == HttpStatusCode.NotFound || code == "woocommerce_rest_product_invalid_id")
            {
                return null;
            }
            throw new WooCommerceApiException(code, msg);
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        var products = JsonSerializer.Deserialize<List<WcProductDto>>(json, JsonOptions);
        return products?.FirstOrDefault();
    }

    private static async Task<(string Code, string Message)> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            var error = JsonSerializer.Deserialize<WcApiErrorDto>(body, JsonOptions);
            return (error?.Code ?? "unknown", error?.Message ?? response.ReasonPhrase ?? "Unknown error");
        }
        catch
        {
            return ("http_error", response.ReasonPhrase ?? $"HTTP {(int)response.StatusCode}");
        }
    }

    public void Dispose() { }

    public WooCommerceSettings GetSettings() => _settings;
}

/// <summary>
/// Exception thrown when a WooCommerce API call fails.
/// </summary>
public class WooCommerceApiException : Exception
{
    public string Code { get; }
    public bool IsValidationError { get; }

    public WooCommerceApiException(string code, string message, bool isValidationError = false)
        : base(message)
    {
        Code = code;
        IsValidationError = isValidationError;
    }
}

/// <summary>Request payload for POST /customers.</summary>
public class WcCustomerCreateRequest
{
    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("first_name")]
    public string? FirstName { get; set; }

    [JsonPropertyName("last_name")]
    public string? LastName { get; set; }

    [JsonPropertyName("billing")]
    public WcAddressDto? Billing { get; set; }

    [JsonPropertyName("shipping")]
    public WcAddressDto? Shipping { get; set; }
}