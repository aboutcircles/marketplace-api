namespace Circles.Market.Api;

/// <summary>
/// Centralized constants for status IRIs used across the Market API.
/// Avoids hardcoded magic strings scattered in code and SQL.
/// </summary>
public static class StatusUris
{
    public const string PaymentProcessing = "https://aboutcircles.com/status/PaymentProcessing";
    public const string PaymentComplete = "https://schema.org/PaymentComplete";
}
