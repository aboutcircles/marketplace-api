using Circles.Market.Auth.Siwe;
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

            var service = CreateService();
            var ex = Assert.ThrowsAsync<ArgumentException>(() => service.CreateChallengeAsync(ctx, payload, "Sign in to Circles Market"));
            Assert.That(ex!.Message, Does.Contain("allowlisted"));
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

            var service = CreateService();
            var res = await service.CreateChallengeAsync(ctx, payload, "Sign in to Circles Market");
            Assert.That(res.Message, Does.Contain("evil.example.net"));
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

            var service = CreateService();
            var res = await service.CreateChallengeAsync(ctx, payload, "Sign in to Circles Market");

            Assert.That(res.Message, Does.Contain("URI: https://market.example.com"));
            Assert.That(res.Message, Does.StartWith("market.example.com wants you"));
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

        // 65-byte signature: 32 bytes r + 32 bytes s + 1 byte v (v=27 is valid)
        var req = new VerifyRequest { ChallengeId = chId, Signature = "0x" + new string('0', 128) + "1b" };

        // Act 1: first verify succeeds
        var result1 = await AuthEndpoints_Verify(req, store.Object, verifier.Object, tokens.Object, CancellationToken.None);
        Assert.That(result1.Success, Is.True);

        // Act 2: second verify fails due to already used
        var result2 = await AuthEndpoints_Verify(req, store.Object, verifier.Object, tokens.Object, CancellationToken.None);
        Assert.That(result2.Success, Is.False);

        store.VerifyAll();
        verifier.VerifyAll();
        tokens.Verify(t => t.Issue(It.IsAny<TokenSubject>(), It.IsAny<TimeSpan>()), Times.Once);
    }

    // Helpers to invoke private static methods via local wrappers
    private static SiweAuthService CreateService()
    {
        var options = new SiweAuthOptions
        {
            AllowedDomainsEnv = "MARKET_AUTH_ALLOWED_DOMAINS",
            PublicBaseUrlEnv = "PUBLIC_BASE_URL",
            ExternalBaseUrlEnv = "EXTERNAL_BASE_URL",
            JwtSecretEnv = "MARKET_JWT_SECRET",
            JwtIssuerEnv = "MARKET_JWT_ISSUER",
            JwtAudienceEnv = "MARKET_JWT_AUDIENCE",
            RequirePublicBaseUrl = false
        };

        var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        return new SiweAuthService(
            options,
            new DummyStore(),
            new Mock<ISafeBytesVerifier>().Object,
            new Mock<ITokenService>().Object,
            loggerFactory);
    }

    private static async Task<(bool Success, VerifyResponse? Response)> AuthEndpoints_Verify(VerifyRequest req, IAuthChallengeStore store, ISafeBytesVerifier safeVerifier, ITokenService tokens, CancellationToken ct)
    {
        var options = new SiweAuthOptions
        {
            AllowedDomainsEnv = "MARKET_AUTH_ALLOWED_DOMAINS",
            PublicBaseUrlEnv = "PUBLIC_BASE_URL",
            ExternalBaseUrlEnv = "EXTERNAL_BASE_URL",
            JwtSecretEnv = "MARKET_JWT_SECRET",
            JwtIssuerEnv = "MARKET_JWT_ISSUER",
            JwtAudienceEnv = "MARKET_JWT_AUDIENCE",
            RequirePublicBaseUrl = false
        };

        var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        var service = new SiweAuthService(options, store, safeVerifier, tokens, loggerFactory);
        try
        {
            var res = await service.VerifyAsync(req, ct);
            return (true, res);
        }
        catch (UnauthorizedAccessException)
        {
            return (false, null);
        }
    }

    // Dummy store used only for CreateChallenge path (save call), not for Verify tests
    private sealed class DummyStore : IAuthChallengeStore
    {
        public Task SaveAsync(AuthChallenge ch, CancellationToken ct = default) => Task.CompletedTask;
        public Task<AuthChallenge?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult<AuthChallenge?>(null);
        public Task<bool> TryMarkUsedAsync(Guid id, CancellationToken ct = default) => Task.FromResult(false);
    }
}
