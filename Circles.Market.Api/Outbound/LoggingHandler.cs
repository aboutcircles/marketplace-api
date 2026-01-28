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
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("HTTP Request: {Method} {Uri}", request.Method, request.RequestUri);
            if (request.Content != null)
            {
                var requestBody = await request.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogDebug("HTTP Request Body: {Body}", requestBody);
            }
        }

        var response = await base.SendAsync(request, cancellationToken);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("HTTP Response: {StatusCode} for {Method} {Uri}", response.StatusCode, request.Method, request.RequestUri);

            try
            {
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
