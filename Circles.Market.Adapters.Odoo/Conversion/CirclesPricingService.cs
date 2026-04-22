using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Circles.Market.Adapters.Odoo.Conversion;

/// <summary>
/// Calls the Circles metrics-api /pricing endpoint to get date-specific
/// dCRC→EUR conversion with demurrage factored in.
///
/// Conversion chain (handled by the API):
///   1. sCRC/xDAI from Balancer
///   2. Demurrage factor: 1 sCRC = conv_factor dCRC
///   3. ∴ 1 dCRC = sCRC/xDAI ÷ conv_factor  (dcrc_xdai)
///   4. xDAI/EUR from ECB
///   5. ∴ 1 dCRC = dcrc_xdai × xdai_eur      (dcrc_eur)
/// </summary>
public sealed class CirclesPricingService : ICirclesPricingService
{
    private readonly HttpClient _http;
    private readonly ILogger<CirclesPricingService> _log;

    public string ApiBaseUrl { get; set; } = "https://rpc.staging.aboutcircles.com/metrics-api";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public CirclesPricingService(HttpClient http, ILogger<CirclesPricingService> log)
    {
        _http = http;
        _log = log;
    }

    public async Task<ConversionSnapshot?> ComputeAsync(
        string orderId,
        string paymentReference,
        string? amountWei,
        string? paymentTimestamp,
        CancellationToken ct = default)
    {
        try
        {
            // --- 1. Validate wei amount ---
            if (string.IsNullOrWhiteSpace(amountWei))
                return null;

            if (!decimal.TryParse(amountWei, NumberStyles.Integer, CultureInfo.InvariantCulture, out var wei))
            {
                _log.LogWarning("Could not parse amountWei '{Wei}' for order {OrderId}", amountWei, orderId);
                return null;
            }

            var crc = wei / 1_000_000_000_000_000_000m;

            // --- 2. Determine pricing date ---
            var priceDate = ExtractDate(paymentTimestamp) ?? DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");

            // --- 3. Call metrics-api pricing ---
            var url = $"{ApiBaseUrl}/pricing?date={priceDate}";
            _log.LogDebug("Fetching pricing from {Url}", url);

            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("Pricing API returned {Status} for date {Date}", resp.StatusCode, priceDate);
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            var pricing = JsonSerializer.Deserialize<PricingResponse>(json, JsonOpts);
            if (pricing == null)
            {
                _log.LogWarning("Could not parse pricing response for date {Date}", priceDate);
                return null;
            }

            // --- 4. Compute EUR equivalent ---
            var eur = crc * pricing.DcrcEur;

            return new ConversionSnapshot
            {
                OrderId = orderId,
                PaymentReference = paymentReference,
                PaymentTimestamp = paymentTimestamp,
                AmountWei = amountWei,
                AmountCrc = crc,
                ScrToXdaiRate = pricing.ScrcXdai,
                ConversionFactor = pricing.ConvFactor,
                DcrcToXdaiRate = pricing.DcrcXdai,
                XdaiToEurRate = pricing.XdaiEur,
                EurEquivalent = eur,
                PriceDate = pricing.Date,
                GeneratedAt = DateTimeOffset.UtcNow,
                PricingSource = pricing.Source
            };
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Conversion snapshot failed for order {OrderId}", orderId);
            return null;
        }
    }

    private static string? ExtractDate(string? timestamp)
    {
        if (string.IsNullOrWhiteSpace(timestamp)) return null;
        return DateTimeOffset.TryParse(timestamp, out var dto)
            ? dto.ToString("yyyy-MM-dd")
            : null;
    }

    private sealed class PricingResponse
    {
        public string Date { get; set; } = string.Empty;
        public decimal ScrcXdai { get; set; }
        public decimal ConvFactor { get; set; }
        public decimal DcrcXdai { get; set; }
        public decimal XdaiEur { get; set; }
        public decimal DcrcEur { get; set; }
        public string Source { get; set; } = string.Empty;
    }
}