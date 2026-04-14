namespace Circles.Market.Adapters.WooCommerce;

/// <summary>
/// Connection settings for a single WooCommerce store, resolved from the
/// <c>wc_connections</c> table at request time.
/// </summary>
public sealed class WooCommerceSettings
{
    public required string BaseUrl { get; init; }
    public required string ConsumerKey { get; init; }
    public required string ConsumerSecret { get; init; }
    public int? DefaultCustomerId { get; init; }
    public string OrderStatus { get; init; } = "pending";
    public int TimeoutMs { get; init; } = 30000;
    public bool FulfillInheritRequestAbort { get; init; } = true;

    /// <summary>Constructs the WooCommerce REST API v3 base URL from the stored <c>wc_base_url</c>.</summary>
    public string ApiBaseUrl => $"{BaseUrl.TrimEnd('/')}/wp-json/wc/v3/";
}

/// <summary>
/// Lightweight DTO returned by <see cref="IWooCommerceConnectionResolver"/> –
/// the minimum fields needed to build a <see cref="WooCommerceClient"/> call.
/// </summary>
public sealed class WooCommerceConnectionInfo
{
    public required string BaseUrl { get; init; }
    public required string ConsumerKey { get; init; }
    public required string ConsumerSecret { get; init; }
    public int? DefaultCustomerId { get; init; }
    public string OrderStatus { get; init; } = "pending";
    public int TimeoutMs { get; init; } = 30000;
    public bool InheritRequestAbort { get; init; } = true;
}

/// <summary>
/// A single row from the <c>wc_connections</c> table.
/// </summary>
public sealed class WooCommerceConnectionEntry
{
    public Guid Id { get; init; }
    public long ChainId { get; init; }
    public string SellerAddress { get; init; } = string.Empty;
    public required string WcBaseUrl { get; init; }
    public required string WcConsumerKey { get; init; }
    public required string WcConsumerSecret { get; init; }
    public int? DefaultCustomerId { get; init; }
    public string OrderStatus { get; init; } = "pending";
    public int TimeoutMs { get; init; } = 30000;
    public bool FulfillInheritRequestAbort { get; init; } = true;
    public bool Enabled { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
}

