using System.Text;
using System.Text.Json;
using Circles.Market.Api.Auth;
using Circles.Profiles.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace Circles.Market.Tests;

[TestFixture]
public class AuthEndpointsTests
{
    private static DefaultHttpContext MakeHttpContext(string scheme, string host)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = scheme;
        ctx.Request.Host = new HostString(host);
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole());
        ctx.RequestServices = services.BuildServiceProvider();
        return ctx;
    }

    private static Stream MakeBody(object payload)
    {
        var ms = new MemoryStream();
        var json = JsonSerializer.Serialize(payload, Circles.Profiles.Models.JsonSerializerOptions.JsonLd);
        var bytes = Encoding.UTF8.GetBytes(json);
        ms.Write(bytes, 0, bytes.Length);
        ms.Position = 0;
        return ms;
    }

    [Test]
    public async Task CreateChallenge_RejectsWhenHostNotAllowlisted()
    {
        var prev = Environment.GetEnvironmentVariable("MARKET_AUTH_ALLOWED_DOMAINS");
        try
        {
            Environment.SetEnvironmentVariable("MARKET_AUTH_ALLOWED_DOMAINS", "market.example.com");
            Environment.SetEnvironmentVariable("PUBLIC_BASE_URL", null);
            Environment.SetEnvironmentVariable("EXTERNAL_BASE_URL", null);

            var ctx = MakeHttpContext("https", "evil.example.net");
            var payload = new ChallengeRequest { Address = "0xAbc0000000000000000000000000000000000000", ChainId = 100 };
            ctx.Request.Body = MakeBody(payload);

            var result = await InvokeCreateChallenge(ctx);
            await result.ExecuteAsync(ctx);

            Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MARKET_AUTH_ALLOWED_DOMAINS", prev);
            Environment.SetEnvironmentVariable("PUBLIC_BASE_URL", null);
            Environment.SetEnvironmentVariable("EXTERNAL_BASE_URL", null);
        }
    }

    [Test]
    public async Task CreateChallenge_AllowsAnyDomainWhenWildcardConfigured()
    {
        var prev = Environment.GetEnvironmentVariable("MARKET_AUTH_ALLOWED_DOMAINS");
        try
        {
            Environment.SetEnvironmentVariable("MARKET_AUTH_ALLOWED_DOMAINS", "*");
            Environment.SetEnvironmentVariable("PUBLIC_BASE_URL", null);
            Environment.SetEnvironmentVariable("EXTERNAL_BASE_URL", null);

            var ctx = MakeHttpContext("https", "evil.example.net");
            var payload = new ChallengeRequest { Address = "0xAbc0000000000000000000000000000000000000", ChainId = 100 };
            ctx.Request.Body = MakeBody(payload);

            var result = await InvokeCreateChallenge(ctx);
            ctx.Response.Body = new MemoryStream();
            await result.ExecuteAsync(ctx);

            Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MARKET_AUTH_ALLOWED_DOMAINS", prev);
            Environment.SetEnvironmentVariable("PUBLIC_BASE_URL", null);
            Environment.SetEnvironmentVariable("EXTERNAL_BASE_URL", null);
        }
    }

    [Test]
    public async Task CreateChallenge_UsesPublicBaseUrlHostInMessage()
    {
        var prevAllowed = Environment.GetEnvironmentVariable("MARKET_AUTH_ALLOWED_DOMAINS");
        var prevPublic = Environment.GetEnvironmentVariable("PUBLIC_BASE_URL");
        try
        {
            Environment.SetEnvironmentVariable("MARKET_AUTH_ALLOWED_DOMAINS", "market.example.com");
            Environment.SetEnvironmentVariable("PUBLIC_BASE_URL", "https://market.example.com");
            Environment.SetEnvironmentVariable("EXTERNAL_BASE_URL", null);

            var ctx = MakeHttpContext("https", "irrelevant.local");
            var payload = new ChallengeRequest { Address = "0xAbc0000000000000000000000000000000000000", ChainId = 100 };
            ctx.Request.Body = MakeBody(payload);

            var result = await InvokeCreateChallenge(ctx);
            ctx.Response.Body = new MemoryStream();
            await result.ExecuteAsync(ctx);

            Assert.That(ctx.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));

            ctx.Response.Body.Position = 0;
            using var reader = new StreamReader(ctx.Response.Body, Encoding.UTF8, leaveOpen: true);
            var text = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(text);
            var msg = doc.RootElement.GetProperty("message").GetString();
            Assert.That(msg, Does.Contain("URI: https://market.example.com"));
            Assert.That(msg, Does.StartWith("market.example.com wants you"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MARKET_AUTH_ALLOWED_DOMAINS", prevAllowed);
            Environment.SetEnvironmentVariable("PUBLIC_BASE_URL", prevPublic);
            Environment.SetEnvironmentVariable("EXTERNAL_BASE_URL", null);
        }
    }

    [Test]
    public async Task Verify_MarksChallengeAtomically_AllowsOnceThen401()
    {
        // Arrange
        var ctx1 = MakeHttpContext("https", "market.example.com");
        var ctx2 = MakeHttpContext("https", "market.example.com");

        var chId = Guid.NewGuid();
        var ch = new AuthChallenge
        {
            Id = chId,
            Address = "0xabc0000000000000000000000000000000000000",
            ChainId = 100,
            Message = "domain wants you to sign in with your Ethereum account:\n0xabc...\n\nSign in to Circles Market\n\nURI: https://market.example.com\nVersion: 1\nChain ID: 100\nNonce: 00\nIssued At: 2020-01-01T00:00:00.0000000Z\nExpiration Time: 2099-01-01T00:00:00.0000000Z",
            IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
        };

        var store = new Mock<IAuthChallengeStore>(MockBehavior.Strict);
        store.Setup(s => s.GetAsync(chId, It.IsAny<CancellationToken>())).ReturnsAsync(ch);
        store.SetupSequence(s => s.TryMarkUsedAsync(chId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)
            .ReturnsAsync(false);

        var verifier = new Mock<ISafeBytesVerifier>(MockBehavior.Strict);
        verifier.Setup(v => v.Verify1271WithBytesAsync(It.IsAny<byte[]>(), ch.Address, It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var tokens = new Mock<ITokenService>(MockBehavior.Strict);
        tokens.Setup(t => t.Issue(It.IsAny<TokenSubject>(), It.IsAny<TimeSpan>())).Returns("tkn");

        var req = new VerifyRequest { ChallengeId = chId, Signature = "0x00" };

        // Act 1: first verify succeeds
        var result1 = await AuthEndpoints_Verify(ctx1, req, store.Object, verifier.Object, tokens.Object, CancellationToken.None);
        await result1.ExecuteAsync(ctx1);
        Assert.That(ctx1.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));

        // Act 2: second verify fails due to already used
        var result2 = await AuthEndpoints_Verify(ctx2, req, store.Object, verifier.Object, tokens.Object, CancellationToken.None);
        await result2.ExecuteAsync(ctx2);
        Assert.That(ctx2.Response.StatusCode, Is.EqualTo(StatusCodes.Status401Unauthorized));

        store.VerifyAll();
        verifier.VerifyAll();
        tokens.Verify(t => t.Issue(It.IsAny<TokenSubject>(), It.IsAny<TimeSpan>()), Times.Once);
    }

    // Helpers to invoke private static methods via local wrappers
    private static Task<IResult> InvokeCreateChallenge(HttpContext ctx)
    {
        // Accessing the private method directly within same assembly is not possible; but it's internal static in same namespace/class.
        // We create a local lambda to call it via reflection.
        var mi = typeof(AuthEndpoints).GetMethod("CreateChallenge", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (Task<IResult>)mi.Invoke(null, new object?[] { ctx, new DummyStore() })!;
    }

    private static Task<IResult> AuthEndpoints_Verify(HttpContext ctx, VerifyRequest req, IAuthChallengeStore store, ISafeBytesVerifier safeVerifier, ITokenService tokens, CancellationToken ct)
    {
        var mi = typeof(AuthEndpoints).GetMethod("Verify", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (Task<IResult>)mi.Invoke(null, new object?[] { ctx, req, store, safeVerifier, tokens, ct })!;
    }

    // Dummy store used only for CreateChallenge path (save call), not for Verify tests
    private sealed class DummyStore : IAuthChallengeStore
    {
        public Task SaveAsync(AuthChallenge ch, CancellationToken ct = default) => Task.CompletedTask;
        public Task<AuthChallenge?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult<AuthChallenge?>(null);
        public Task<bool> TryMarkUsedAsync(Guid id, CancellationToken ct = default) => Task.FromResult(false);
    }
}
