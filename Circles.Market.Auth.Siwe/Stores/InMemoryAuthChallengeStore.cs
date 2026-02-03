using System.Collections.Concurrent;

namespace Circles.Market.Auth.Siwe;

public sealed class InMemoryAuthChallengeStore : IAuthChallengeStore
{
    private readonly ConcurrentDictionary<Guid, AuthChallenge> _store = new();

    public Task SaveAsync(AuthChallenge ch, CancellationToken ct = default)
    {
        _store[ch.Id] = ch;
        return Task.CompletedTask;
    }

    public Task<AuthChallenge?> GetAsync(Guid id, CancellationToken ct = default)
    {
        _store.TryGetValue(id, out var ch);
        return Task.FromResult(ch);
    }

    public Task<bool> TryMarkUsedAsync(Guid id, CancellationToken ct = default)
    {
        if (!_store.TryGetValue(id, out var ch))
            return Task.FromResult(false);

        if (ch.UsedAt is not null || ch.ExpiresAt < DateTimeOffset.UtcNow)
            return Task.FromResult(false);

        var updated = new AuthChallenge
        {
            Id = ch.Id,
            Address = ch.Address,
            ChainId = ch.ChainId,
            Nonce = ch.Nonce,
            Message = ch.Message,
            IssuedAt = ch.IssuedAt,
            ExpiresAt = ch.ExpiresAt,
            UsedAt = DateTimeOffset.UtcNow,
            UserAgent = ch.UserAgent,
            Ip = ch.Ip
        };

        bool ok = _store.TryUpdate(id, updated, ch);
        return Task.FromResult(ok);
    }
}
