using Circles.Market.Api;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Circles.Market.Tests;

[TestFixture]
public class PublicCorsPolicyTests
{
    // Runs the policy the way the CORS middleware does and returns the response
    // headers a browser would see for a GET from the given origin.
    private static IHeaderDictionary ResponseHeadersFor(string origin)
    {
        var builder = new CorsPolicyBuilder();
        PublicCorsPolicy.Configure(builder);
        var service = new CorsService(Options.Create(new CorsOptions()), NullLoggerFactory.Instance);

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Headers.Origin = origin;

        var result = service.EvaluatePolicy(ctx, builder.Build());
        service.ApplyResult(result, ctx.Response);
        return ctx.Response.Headers;
    }

    [Test]
    public void ForeignOrigin_IsNotAllowedToSendCredentials()
    {
        var headers = ResponseHeadersFor("https://evil.example");

        Assert.That(headers.ContainsKey("Access-Control-Allow-Credentials"), Is.False);
        Assert.That(headers["Access-Control-Allow-Origin"].ToString(), Is.EqualTo("*"));
    }

    [Test]
    public void AnyOrigin_CanStillReadThePublicApi()
    {
        var headers = ResponseHeadersFor("https://app.aboutcircles.com");

        Assert.That(headers["Access-Control-Allow-Origin"].ToString(), Is.EqualTo("*"));
    }

    [Test]
    public void PaginationHeaders_StayExposed()
    {
        var headers = ResponseHeadersFor("https://app.aboutcircles.com");

        Assert.That(headers["Access-Control-Expose-Headers"].ToString(),
            Does.Contain("X-Next-Cursor").And.Contain("Link"));
    }
}
