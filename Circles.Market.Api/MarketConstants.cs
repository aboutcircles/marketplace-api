namespace Circles.Market.Api;

/// <summary>
/// Central place for common constants used across the Market API project.
/// Collects content types, routes, limits, defaults and header names to avoid magic literals.
/// </summary>
public static class MarketConstants
{
    public static class ContentTypes
    {
        public const string JsonLdUtf8 = "application/ld+json; charset=utf-8";
        public const string Json = "application/json";
        public const string OctetStream = "application/octet-stream";
        public const string AnyImage = "image/*";
    }

    public static class Headers
    {
        public const string XContentTypeOptions = "X-Content-Type-Options";
        public const string NoSniff = "nosniff";
        public const string Link = "Link";
        public const string XNextCursor = "X-Next-Cursor";
    }

    public static class Routes
    {
        public const string CartBase = "/api/cart/v1";
        public const string Pin = "/api/pin";
        public const string PinMedia = "/api/pin-media";
        public const string Canonicalize = "/api/canonicalize";
        public const string AvailabilityBase = "/inventory/availability";
        public const string InventoryBase = "/inventory/inventory";
    }

    public static class Defaults
    {
        public const long ChainId = 100;
        public const int PageSize = 20;
        public const int PageSizeMin = 1;
        public const int PageSizeMax = 100;
        public const long WindowStart = 0; // unix seconds
    }

    public static class Limits
    {
        // 8 MiB cap for uploads (pin and canonicalize endpoints)
        public static int MaxUploadBytes => Circles.Profiles.Models.ProtocolLimits.MaxObjectBytes;
    }

    public static class IdPrefixes
    {
        public const string Order = "ord_";
        public const string PaymentReference = "pay_";
    }
}
