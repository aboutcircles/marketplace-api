using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Circles.Market.Api.Payments;

public interface IOrderStatusEventBus
{
    // Buyer-scoped subscription
    IAsyncEnumerable<OrderStatusEvent> SubscribeAsync(
        string buyerAddress,
        long chainId,
        CancellationToken ct = default);

    // Seller-scoped subscription
    IAsyncEnumerable<OrderStatusEvent> SubscribeForSellerAsync(
        string sellerAddress,
        long chainId,
        CancellationToken ct = default);

    Task PublishAsync(OrderStatusEvent evt, CancellationToken ct = default);
}

public sealed class InMemoryOrderStatusEventBus : IOrderStatusEventBus
{
    private readonly ConcurrentDictionary<(string buyer, long chain), ConcurrentDictionary<Channel<OrderStatusEvent>, byte>> _buyerSubs =
        new();
    private readonly ConcurrentDictionary<(string seller, long chain), ConcurrentDictionary<Channel<OrderStatusEvent>, byte>> _sellerSubs =
        new();

    // Guards outer-dictionary lifecycle so we never remove a group while another thread
    // is in the middle of GetOrAdd + Add for the same key.
    private readonly object _subsGate = new();

    private readonly int _capacity;
    private readonly int _maxSubsPerKey;

    public InMemoryOrderStatusEventBus()
    {
        _capacity = int.TryParse(Environment.GetEnvironmentVariable("SSE_CHANNEL_CAPACITY"), out var cap) && cap > 0
            ? cap
            : 100;
        _maxSubsPerKey = int.TryParse(Environment.GetEnvironmentVariable("SSE_MAX_SUBSCRIBERS_PER_KEY"), out var ms) && ms > 0
            ? ms
            : 100;
    }

    public Task PublishAsync(OrderStatusEvent evt, CancellationToken ct = default)
    {
        if (evt is null)
        {
            return Task.CompletedTask;
        }

        // Publish to buyer group if present
        string buyer = evt.BuyerAddress?.ToLowerInvariant() ?? string.Empty;
        long buyerChain = evt.BuyerChainId ?? 0;
        if (!string.IsNullOrWhiteSpace(buyer) && buyerChain > 0)
        {
            if (_buyerSubs.TryGetValue((buyer, buyerChain), out var channels))
            {
                foreach (var ch in channels.Keys)
                {
                    try
                    {
                        // Non-blocking publish; channel is bounded with DropOldest
                        ch.Writer.TryWrite(evt);
                    }
                    catch
                    {
                        // ignore write errors; reader may be gone
                    }
                }
            }
        }

        // Publish to seller group if present
        string seller = evt.SellerAddress?.ToLowerInvariant() ?? string.Empty;
        long sellerChain = evt.SellerChainId ?? 0;
        if (!string.IsNullOrWhiteSpace(seller) && sellerChain > 0)
        {
            if (_sellerSubs.TryGetValue((seller, sellerChain), out var channels))
            {
                foreach (var ch in channels.Keys)
                {
                    try
                    {
                        ch.Writer.TryWrite(evt);
                    }
                    catch
                    {
                        // ignore write errors; reader may be gone
                    }
                }
            }
        }

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<OrderStatusEvent> SubscribeAsync(
        string buyerAddress,
        long chainId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        bool hasBuyer = !string.IsNullOrWhiteSpace(buyerAddress);
        bool hasChain = chainId > 0;
        if (!hasBuyer || !hasChain)
        {
            yield break;
        }

        var key = (buyer: buyerAddress.ToLowerInvariant(), chain: chainId);

        var channel = Channel.CreateBounded<OrderStatusEvent>(new BoundedChannelOptions(_capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false
        });

        bool added = false;
        lock (_subsGate)
        {
            var set = _buyerSubs.GetOrAdd(
                key,
                _ => new ConcurrentDictionary<Channel<OrderStatusEvent>, byte>());

            if (set.Count < _maxSubsPerKey)
            {
                set.TryAdd(channel, 0);
                added = true;
            }
        }

        if (!added)
        {
            channel.Writer.TryComplete();
            yield break;
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                bool hasData;
                try
                {
                    hasData = await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    yield break;
                }

                if (!hasData)
                {
                    yield break;
                }

                while (channel.Reader.TryRead(out var item))
                {
                    yield return item;
                }
            }
        }
        finally
        {
            lock (_subsGate)
            {
                if (_buyerSubs.TryGetValue(key, out var current) && current is not null)
                {
                    current.TryRemove(channel, out _);
                    if (current.IsEmpty)
                    {
                        _buyerSubs.TryRemove(key, out _);
                    }
                }
            }
        }
    }

    public async IAsyncEnumerable<OrderStatusEvent> SubscribeForSellerAsync(
        string sellerAddress,
        long chainId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        bool hasSeller = !string.IsNullOrWhiteSpace(sellerAddress);
        bool hasChain = chainId > 0;
        if (!hasSeller || !hasChain)
        {
            yield break;
        }

        var key = (seller: sellerAddress.ToLowerInvariant(), chain: chainId);

        var channel = Channel.CreateBounded<OrderStatusEvent>(new BoundedChannelOptions(_capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false
        });

        bool added = false;
        lock (_subsGate)
        {
            var set = _sellerSubs.GetOrAdd(
                key,
                _ => new ConcurrentDictionary<Channel<OrderStatusEvent>, byte>());

            if (set.Count < _maxSubsPerKey)
            {
                set.TryAdd(channel, 0);
                added = true;
            }
        }

        if (!added)
        {
            channel.Writer.TryComplete();
            yield break;
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                bool hasData;
                try
                {
                    hasData = await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    yield break;
                }

                if (!hasData)
                {
                    yield break;
                }

                while (channel.Reader.TryRead(out var item))
                {
                    yield return item;
                }
            }
        }
        finally
        {
            lock (_subsGate)
            {
                if (_sellerSubs.TryGetValue(key, out var current) && current is not null)
                {
                    current.TryRemove(channel, out _);
                    if (current.IsEmpty)
                    {
                        _sellerSubs.TryRemove(key, out _);
                    }
                }
            }
        }
    }
}
