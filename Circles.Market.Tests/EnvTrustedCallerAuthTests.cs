using Circles.Market.Adapters.CodeDispenser.Auth;
using Circles.Market.Adapters.Odoo.Auth;
using Circles.Market.Adapters.WooCommerce.Auth;
using Microsoft.Extensions.Logging.Abstractions;

namespace Circles.Market.Tests;

[NonParallelizable]
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

    // ── WooCommerce ───────────────────────────────────────────────────────────

    [Test]
    public async Task WooCommerce_Denies_Invalid_Secret()
    {
        Environment.SetEnvironmentVariable(EnvVar, "correct-secret-123456");
        var auth = new Circles.Market.Adapters.WooCommerce.Auth.EnvTrustedCallerAuth(
            NullLogger<Circles.Market.Adapters.WooCommerce.Auth.EnvTrustedCallerAuth>.Instance);

        var res = await auth.AuthorizeAsync("wrong", "inventory", 100, "0xabc", CancellationToken.None);

        Assert.That(res.Allowed, Is.False);
    }

    [Test]
    public async Task WooCommerce_Allows_Valid_Secret()
    {
        Environment.SetEnvironmentVariable(EnvVar, "correct-secret-123456");
        var auth = new Circles.Market.Adapters.WooCommerce.Auth.EnvTrustedCallerAuth(
            NullLogger<Circles.Market.Adapters.WooCommerce.Auth.EnvTrustedCallerAuth>.Instance);

        var res = await auth.AuthorizeAsync("correct-secret-123456", "inventory", 100, "0xabc", CancellationToken.None);

        Assert.That(res.Allowed, Is.True);
        Assert.That(res.CallerId, Is.EqualTo("env"));
    }

    [Test]
    public void WooCommerce_Throws_WhenEnvVarNotSet()
    {
        Environment.SetEnvironmentVariable(EnvVar, null);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new Circles.Market.Adapters.WooCommerce.Auth.EnvTrustedCallerAuth(
                NullLogger<Circles.Market.Adapters.WooCommerce.Auth.EnvTrustedCallerAuth>.Instance));

        Assert.That(ex!.Message, Does.Contain("CIRCLES_SERVICE_KEY"));
    }
}
