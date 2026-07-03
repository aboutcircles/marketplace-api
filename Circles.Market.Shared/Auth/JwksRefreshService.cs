using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Circles.Market.Shared.Auth;

/// <summary>
/// Keeps the <see cref="JwksKeyManager"/> snapshot warm so token validation never performs
/// I/O. Warms up synchronously at host start (a failure logs and defers to the background
/// loop), then refreshes at half the cache duration with jitter, tightening to a short
/// retry interval while the cache is expired or empty.
/// Accepted tradeoff of cache-only reads: if the auth service is down exactly at boot,
/// token validation fails until a background retry succeeds (bounded to one 12–18s retry
/// cycle); the token-exchange fallback masks that window for exchangeable tokens.
/// </summary>
public sealed class JwksRefreshService : BackgroundService
{
    private static readonly TimeSpan FailureRetryInterval = TimeSpan.FromSeconds(15);

    private readonly JwksKeyManager _keys;
    private readonly ILogger<JwksRefreshService> _log;

    public JwksRefreshService(JwksKeyManager keys, ILogger<JwksRefreshService> log)
    {
        _keys = keys;
        _log = log;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _keys.RefreshAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "JWKS warm-up fetch failed; starting without cached keys, background refresh will retry");
        }

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay = ComputeNextDelay();

            try
            {
                await Task.Delay(delay, stoppingToken);
                await _keys.RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (_keys.IsFresh)
                {
                    _log.LogWarning(ex, "Background JWKS refresh failed; will retry");
                }
                else
                {
                    _log.LogError(ex,
                        "Background JWKS refresh failed with no fresh keys; staleness window is burning down, will retry");
                }
            }
        }
    }

    /// <summary>
    /// Half the cache duration while the snapshot is fresh, a short retry interval
    /// otherwise — with ±20% jitter so multiple hosts don't hammer the auth service
    /// in lockstep.
    /// </summary>
    internal TimeSpan ComputeNextDelay()
    {
        TimeSpan baseDelay = _keys.IsFresh ? _keys.CacheDuration / 2 : FailureRetryInterval;
        return baseDelay * (0.8 + Random.Shared.NextDouble() * 0.4);
    }
}
