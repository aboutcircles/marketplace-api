using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Circles.Market.Api.Outbound;

public class LoggingHandler : DelegatingHandler
{
    private readonly ILogger _logger;

    public LoggingHandler(ILogger logger)
    {
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            try
            {
                // We don't want to consume the stream if it's not already buffered,
                // but in this project most responses are read via ReadWithLimitAsync anyway.
                // However, the handler runs before the caller gets the response.
                // If we read it here, we might break the caller if they expect to read the stream themselves.
                // But for logging the first 500 chars, we can try to peek or use LoadIntoBufferAsync.

                if (response.Content != null)
                {
                    await response.Content.LoadIntoBufferAsync();
                    var content = await response.Content.ReadAsStringAsync(cancellationToken);
                    var logContent = content.Length > 500 ? content.Substring(0, 500) + "..." : content;
                    _logger.LogDebug("HTTP Response Body (first 500 chars): {Body}", logContent);
                }
            }
            catch (System.Exception ex)
            {
                _logger.LogDebug(ex, "Failed to log response body");
            }
        }

        return response;
    }
}
