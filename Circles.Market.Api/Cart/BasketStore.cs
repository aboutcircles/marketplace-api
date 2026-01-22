using System.Collections.Concurrent;
using System.Text.Json;

namespace Circles.Market.Api.Cart;

public interface IBasketStore
{
    Basket Create(string? operatorAddr, string? buyerAddr, long? chainId);
    (Basket basket, bool expired)? Get(string basketId);
    Basket Patch(string basketId, Action<Basket> patch);
    bool TryFreeze(string basketId);
    Basket? TryFreezeAndRead(string basketId);
    Basket? TryFreezeAndRead(string basketId, long expectedVersion);
}

internal class BasketRecord
{
    public Basket Basket { get; set; } = new();
    public DateTimeOffset ExpiresAt { get; set; }
    public object Gate { get; } = new();
}

public class InMemoryBasketStore : IBasketStore
{
    private readonly ConcurrentDictionary<string, BasketRecord> _baskets = new();

    private static string NewId(string prefix)
        => prefix + Guid.NewGuid().ToString("N").Substring(0, 22);

    public Basket Create(string? operatorAddr, string? buyerAddr, long? chainId)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string id = NewId("bkt_");
        var b = new Basket
        {
            BasketId = id,
            Operator = operatorAddr,
            Buyer = buyerAddr,
            ChainId = chainId ?? 100,
            Status = nameof(BasketStatus.Draft),
            CreatedAt = now,
            ModifiedAt = now,
            TtlSeconds = 86400,
            Version = 0
        };

        var rec = new BasketRecord
        {
            Basket = b,
            ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(now).AddSeconds(b.TtlSeconds)
        };
        _baskets[id] = rec;
        return b;
    }

    public (Basket basket, bool expired)? Get(string basketId)
    {
        if (!_baskets.TryGetValue(basketId, out var rec)) return null;
        lock (rec.Gate)
        {
            bool expired = DateTimeOffset.UtcNow >= rec.ExpiresAt || string.Equals(rec.Basket.Status, nameof(BasketStatus.Expired), StringComparison.Ordinal);
            // Return a deep clone to avoid external mutations of internal state
            var cloneJson = JsonSerializer.Serialize(rec.Basket, Circles.Profiles.Models.JsonSerializerOptions.JsonLd);
            var clone = JsonSerializer.Deserialize<Basket>(cloneJson, Circles.Profiles.Models.JsonSerializerOptions.JsonLd)!;
            return (clone, expired);
        }
    }

    public Basket Patch(string basketId, Action<Basket> patch)
    {
        if (!_baskets.TryGetValue(basketId, out var rec)) throw new KeyNotFoundException();
        lock (rec.Gate)
        {
            if (rec.Basket.Status is nameof(BasketStatus.CheckedOut)) throw new InvalidOperationException("Basket already checked out");
            // Work on a copy to avoid exposing partially updated state on exceptions
            var currentJson = JsonSerializer.Serialize(rec.Basket, Circles.Profiles.Models.JsonSerializerOptions.JsonLd);
            var working = JsonSerializer.Deserialize<Basket>(currentJson, Circles.Profiles.Models.JsonSerializerOptions.JsonLd)!;
            patch(working);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            working.ModifiedAt = now;
            working.Version++;
            rec.Basket = working;
            rec.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(rec.Basket.TtlSeconds);
            // Return a deep clone
            var cloneJson = JsonSerializer.Serialize(rec.Basket, Circles.Profiles.Models.JsonSerializerOptions.JsonLd);
            return JsonSerializer.Deserialize<Basket>(cloneJson, Circles.Profiles.Models.JsonSerializerOptions.JsonLd)!;
        }
    }

    public bool TryFreeze(string basketId)
    {
        if (!_baskets.TryGetValue(basketId, out var rec)) return false;
        lock (rec.Gate)
        {
            if (rec.Basket.Status is nameof(BasketStatus.CheckedOut)) return false;
            rec.Basket.Status = nameof(BasketStatus.CheckedOut);
            rec.Basket.Version++;
            return true;
        }
    }

    public Basket? TryFreezeAndRead(string basketId)
    {
        if (!_baskets.TryGetValue(basketId, out var rec)) return null;
        lock (rec.Gate)
        {
            if (rec.Basket.Status is nameof(BasketStatus.CheckedOut)) return null;
            rec.Basket.Status = nameof(BasketStatus.CheckedOut);
            rec.Basket.Version++;
            // Deep clone to avoid exposing internal reference
            var json = System.Text.Json.JsonSerializer.Serialize(rec.Basket, Circles.Profiles.Models.JsonSerializerOptions.JsonLd);
            return System.Text.Json.JsonSerializer.Deserialize<Basket>(json, Circles.Profiles.Models.JsonSerializerOptions.JsonLd)!;
        }
    }

    public Basket? TryFreezeAndRead(string basketId, long expectedVersion)
    {
        if (!_baskets.TryGetValue(basketId, out var rec)) return null;
        lock (rec.Gate)
        {
            if (rec.Basket.Status is nameof(BasketStatus.CheckedOut)) return null;
            if (rec.Basket.Version != expectedVersion) return null;
            rec.Basket.Status = nameof(BasketStatus.CheckedOut);
            rec.Basket.Version++;
            // Deep clone
            var json = System.Text.Json.JsonSerializer.Serialize(rec.Basket, Circles.Profiles.Models.JsonSerializerOptions.JsonLd);
            return System.Text.Json.JsonSerializer.Deserialize<Basket>(json, Circles.Profiles.Models.JsonSerializerOptions.JsonLd)!;
        }
    }
}
