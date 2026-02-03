using Circles.Market.Adapters.CodeDispenser.Auth;
using Circles.Market.Adapters.Odoo.Auth;
using Microsoft.Extensions.Logging.Abstractions;

namespace Circles.Market.Tests;

public class EnvTrustedCallerAuthTests
{
    private const string EnvVar = "CIRCLES_SERVICE_KEY";

    [Test]
    public async Task CodeDispenser_Denies_Invalid_Secret()
    {
        Environment.SetEnvironmentVariable(EnvVar, "correct-secret-123456");
        var auth = new Circles.Market.Adapters.CodeDispenser.Auth.EnvTrustedCallerAuth(
            NullLogger<Circles.Market.Adapters.CodeDispenser.Auth.EnvTrustedCallerAuth>.Instance);

        var res = await auth.AuthorizeAsync("wrong", "inventory", 100, "0xabc", CancellationToken.None);

        Assert.That(res.Allowed, Is.False);
    }

    [Test]
    public async Task CodeDispenser_Allows_Valid_Secret()
    {
        Environment.SetEnvironmentVariable(EnvVar, "correct-secret-123456");
        var auth = new Circles.Market.Adapters.CodeDispenser.Auth.EnvTrustedCallerAuth(
            NullLogger<Circles.Market.Adapters.CodeDispenser.Auth.EnvTrustedCallerAuth>.Instance);

        var res = await auth.AuthorizeAsync("correct-secret-123456", "inventory", 100, "0xabc", CancellationToken.None);

        Assert.That(res.Allowed, Is.True);
        Assert.That(res.CallerId, Is.EqualTo("env"));
    }

    [Test]
    public async Task Odoo_Denies_Invalid_Secret()
    {
        Environment.SetEnvironmentVariable(EnvVar, "correct-secret-123456");
        var auth = new Circles.Market.Adapters.Odoo.Auth.EnvTrustedCallerAuth(
            NullLogger<Circles.Market.Adapters.Odoo.Auth.EnvTrustedCallerAuth>.Instance);

        var res = await auth.AuthorizeAsync("wrong", "inventory", 100, "0xabc", CancellationToken.None);

        Assert.That(res.Allowed, Is.False);
    }

    [Test]
    public async Task Odoo_Allows_Valid_Secret()
    {
        Environment.SetEnvironmentVariable(EnvVar, "correct-secret-123456");
        var auth = new Circles.Market.Adapters.Odoo.Auth.EnvTrustedCallerAuth(
            NullLogger<Circles.Market.Adapters.Odoo.Auth.EnvTrustedCallerAuth>.Instance);

        var res = await auth.AuthorizeAsync("correct-secret-123456", "inventory", 100, "0xabc", CancellationToken.None);

        Assert.That(res.Allowed, Is.True);
        Assert.That(res.CallerId, Is.EqualTo("env"));
    }
}
