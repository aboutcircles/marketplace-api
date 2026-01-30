using Circles.Market.Api.Inventory;
using Circles.Market.Api.Routing;
using Circles.Profiles.Models.Market;
using Microsoft.Extensions.Caching.Memory;

namespace Circles.Market.Api.Cart;

public sealed class BasketCanonicalizer : IBasketCanonicalizer
{
    private readonly IProductResolver _resolver;
    private readonly ILiveInventoryClient _inventoryClient;
    private readonly IMarketRouteStore _routes;
    private readonly IMemoryCache _cache;
    private readonly Microsoft.Extensions.Logging.ILogger<BasketCanonicalizer>? _logger;

    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(5);

    private sealed record InventoryState(string? InventoryFeed, long RequestedQuantity, long? Available);

    public BasketCanonicalizer(
        IProductResolver resolver,
        ILiveInventoryClient inventoryClient,
        IMarketRouteStore routes,
        IMemoryCache cache,
        Microsoft.Extensions.Logging.ILogger<BasketCanonicalizer>? logger = null)
    {
        _resolver = resolver;
        _inventoryClient = inventoryClient;
        _routes = routes ?? throw new ArgumentNullException(nameof(routes));
        _cache = cache;
        _logger = logger;
    }

    public async Task CanonicalizeAsync(Basket basket, CancellationToken ct = default)
    {
        if (basket is null) throw new ArgumentNullException(nameof(basket));

        if (basket.Items.Count == 0) return;

        var itemsHash = BasketHashing.ItemsHash(basket);
        var key = $"canon:{basket.BasketId}:{itemsHash}";
        var now = DateTimeOffset.UtcNow;

        if (_cache.TryGetValue<CanonicalSnapshot>(key, out var snap))
        {
            var age = now - snap!.FetchedAt;
            ApplySnapshotTo(basket, snap.Basket);

            if (age <= Fresh)
            {
                return;
            }

            if (age <= Stale)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var refreshed = await CanonicalizeLiveAsync(CloneForCanonicalization(basket), CancellationToken.None);
                        _cache.Set(key, new CanonicalSnapshot(refreshed, DateTimeOffset.UtcNow), new MemoryCacheEntryOptions
                        {
                            AbsoluteExpirationRelativeToNow = Stale,
                            Size = EstimateSize(refreshed)
                        });
                    }
                    catch (OperationCanceledException)
                    {
                        // best-effort background refresh canceled: do not log as error
                    }
                    catch (Exception ex)
                    {
                        // best-effort background refresh failed: log at Debug with context
                        _logger?.LogDebug(ex, "Background canonicalization failed (basketId={BasketId}, key={CacheKey}, itemsHash={ItemsHash})", basket.BasketId, key, itemsHash);
                    }
                });
                return;
            }
            // else: very old → fall through to blocking refresh
        }

        var live = await CanonicalizeLiveAsync(CloneForCanonicalization(basket), ct);
        _cache.Set(key, new CanonicalSnapshot(live, now), new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = Stale,
            Size = EstimateSize(live)
        });
        ApplySnapshotTo(basket, live);
        return;
    }

    private async Task<Basket> CanonicalizeLiveAsync(Basket basket, CancellationToken ct)
    {
        // Operator-aware resolution
        string? op = basket.Operator is null ? null : Utils.NormalizeAddr(basket.Operator);

        // Aggregate per (seller, sku)
        var perProduct = new Dictionary<(string seller, string sku), InventoryState>();

        foreach (var line in basket.Items)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(line.Seller))
            {
                throw new ArgumentException("Each basket item must have a seller address", nameof(basket));
            }

            if (line.OrderedItem?.Sku is null || string.IsNullOrWhiteSpace(line.OrderedItem.Sku))
            {
                throw new ArgumentException("Each basket item must have an orderedItem.sku", nameof(basket));
            }

            string seller = Utils.NormalizeAddr(line.Seller!);
            string sku = line.OrderedItem.Sku!.Trim();

            var (product, cid) = await _resolver.ResolveProductAsync(basket.ChainId, seller, op, sku, ct);

            if (product is null)
            {
                throw new InvalidOperationException(
                    $"Product not found for seller={seller}, sku={sku}, chainId={basket.ChainId}");
            }

            var offer = product.Offers.FirstOrDefault();
            if (offer is null)
            {
                throw new InvalidOperationException($"Product {sku} for seller={seller} has no offers");
            }

            // Look up DB config for routing
            string skuCfg = product.Sku.Trim().ToLowerInvariant();
            var cfg = await _routes.TryGetAsync(basket.ChainId, seller, skuCfg, ct);
            if (cfg is null || !cfg.IsConfigured)
            {
                throw new InvalidOperationException(
                    $"Product not configured for seller={seller}, sku={skuCfg}, chainId={basket.ChainId}");
            }

            // Inventory enforcement
            var skuKey = product.Sku.Trim().ToLowerInvariant();
            var key = (seller, skuKey);
            perProduct.TryGetValue(key, out var state);

            var invFeed = await _routes.TryResolveUpstreamAsync(
                basket.ChainId,
                seller,
                skuCfg,
                MarketServiceKind.Inventory,
                ct);
            long qty = line.OrderQuantity <= 0 ? 1 : line.OrderQuantity;
            // Normalize quantity defensively so downstream totals and snapshots never see <= 0
            line.OrderQuantity = (int)qty;

            if (state is null)
            {
                long? available = null;
                if (!string.IsNullOrWhiteSpace(invFeed))
                {
                    var res = await _inventoryClient.FetchInventoryAsync(invFeed!, ct);
                    if (res.IsError)
                    {
                        throw new InvalidOperationException(
                            $"inventoryFeed error for seller={seller}, sku={product.Sku}: {res.Error}");
                    }

                    available = res.Value!.Value;
                }

                state = new InventoryState(invFeed, qty, available);
            }
            else
            {
                long newRequested = state.RequestedQuantity + qty;
                state = state with { RequestedQuantity = newRequested };
            }

            if (state.Available is long avail && state.RequestedQuantity > avail)
            {
                throw new InvalidOperationException(
                    $"Requested quantity {state.RequestedQuantity} for seller={seller}, sku={product.Sku} exceeds inventory {avail}.");
            }

            // Determine if this is a one-off offer based on DB config
            bool isOneOff = cfg.IsOneOff;

            // Enforce quantity = 1 for one-off items
            if (isOneOff && state.RequestedQuantity > 1)
            {
                throw new InvalidOperationException(
                    $"One-off items cannot be ordered with quantity > 1. Requested quantity {state.RequestedQuantity} for seller={seller}, sku={product.Sku}.");
            }

            perProduct[key] = state;

            // Rewrite to canonical values
            line.Seller = seller;
            line.OrderedItem.Sku = product.Sku;
            line.ProductCid = cid;

            line.OfferSnapshot = new OfferSnapshot
            {
                Type = "Offer",
                Price = offer.Price,
                PriceCurrency = offer.PriceCurrency,
                // Always derive seller identity from service inputs to prevent upstream tampering
                Seller = new SchemaOrgOrgId
                {
                    Type = "Organization",
                    Id = $"eip155:{basket.ChainId}:{seller}"
                },
                AvailableDeliveryMethod = offer.AvailableDeliveryMethod?.ToList(),
                RequiredSlots = offer.RequiredSlots?.ToList(),
                // Fulfillment endpoint is resolved from DB at fulfillment time
                CirclesFulfillmentEndpoint = null,
                CirclesFulfillmentTrigger = offer.CirclesFulfillmentTrigger,
                IsOneOff = isOneOff
            };
        }

        return basket;
    }

    private static void ApplySnapshotTo(Basket target, Basket snapshot)
    {
        if (target.Items.Count != snapshot.Items.Count)
        {
            return;
        }

        for (int i = 0; i < target.Items.Count; i++)
        {
            var t = target.Items[i];
            var s = snapshot.Items[i];

            t.Seller = s.Seller;
            // Propagate canonicalized quantity so normalization sticks in the live basket
            t.OrderQuantity = s.OrderQuantity;
            t.OrderedItem ??= new OrderedItemRef();
            t.OrderedItem.Sku = s.OrderedItem!.Sku;
            t.ProductCid = s.ProductCid;
            t.OfferSnapshot = s.OfferSnapshot is null ? null : new OfferSnapshot
            {
                Type = s.OfferSnapshot.Type,
                Price = s.OfferSnapshot.Price,
                PriceCurrency = s.OfferSnapshot.PriceCurrency,
                Seller = s.OfferSnapshot.Seller is null ? null : new SchemaOrgOrgId
                {
                    Type = s.OfferSnapshot.Seller.Type,
                    Id = s.OfferSnapshot.Seller.Id
                },
                AvailableDeliveryMethod = s.OfferSnapshot.AvailableDeliveryMethod?.ToList(),
                RequiredSlots = s.OfferSnapshot.RequiredSlots?.ToList(),
                // Fulfillment endpoint is resolved from DB at fulfillment time
                CirclesFulfillmentEndpoint = null,
                CirclesFulfillmentTrigger = s.OfferSnapshot.CirclesFulfillmentTrigger,
                IsOneOff = s.OfferSnapshot.IsOneOff
            };
        }
    }

    private static Basket CloneForCanonicalization(Basket src)
    {
        var clone = new Basket
        {
            Context = src.Context,
            Type = src.Type,
            BasketId = src.BasketId,
            Buyer = src.Buyer,
            Operator = src.Operator,
            ChainId = src.ChainId,
            Status = src.Status,
            ShippingAddress = src.ShippingAddress,
            BillingAddress = src.BillingAddress,
            AgeProof = src.AgeProof,
            ContactPoint = src.ContactPoint,
            CreatedAt = src.CreatedAt,
            ModifiedAt = src.ModifiedAt,
            TtlSeconds = src.TtlSeconds,
            Items = new List<OrderItemPreview>(src.Items.Count)
        };

        foreach (var it in src.Items)
        {
            var clonedOrdered = it.OrderedItem is null
                ? null
                : new OrderedItemRef { Type = it.OrderedItem.Type, Sku = it.OrderedItem.Sku };

            clone.Items.Add(new OrderItemPreview
            {
                Type = it.Type,
                OrderQuantity = it.OrderQuantity,
                OrderedItem = clonedOrdered!,
                Seller = it.Seller,
                ImageUrl = it.ImageUrl,
                ProductCid = it.ProductCid,
                OfferSnapshot = it.OfferSnapshot is null ? null : new OfferSnapshot
                {
                    Type = it.OfferSnapshot.Type,
                    Price = it.OfferSnapshot.Price,
                    PriceCurrency = it.OfferSnapshot.PriceCurrency,
                    Seller = it.OfferSnapshot.Seller is null ? null : new SchemaOrgOrgId
                    {
                        Type = it.OfferSnapshot.Seller.Type,
                        Id = it.OfferSnapshot.Seller.Id
                    },
                    AvailableDeliveryMethod = it.OfferSnapshot.AvailableDeliveryMethod?.ToList(),
                    RequiredSlots = it.OfferSnapshot.RequiredSlots?.ToList(),
                    // Fulfillment endpoint is resolved from DB at fulfillment time
                    CirclesFulfillmentEndpoint = null,
                    CirclesFulfillmentTrigger = it.OfferSnapshot.CirclesFulfillmentTrigger,
                    IsOneOff = it.OfferSnapshot.IsOneOff
                }
            });
        }

        return clone;
    }

    private static long EstimateSize(Basket b)
    {
        return 512 + b.Items.Count * 256;
    }
}
