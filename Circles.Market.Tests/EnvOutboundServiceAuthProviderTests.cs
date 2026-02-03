using Circles.Market.Api.Auth;
using Microsoft.Extensions.Logging.Abstractions;

namespace Circles.Market.Tests;

[TestFixture]
public class EnvOutboundServiceAuthProviderTests
{
    [Test]
    public async Task TryGetHeaderAsync_ReturnsHeader_For_Odoo_Origin_When_Token_Set()
    {
        var prevPort = Environment.GetEnvironmentVariable("MARKET_ODOO_ADAPTER_PORT");
        var prevToken = Environment.GetEnvironmentVariable("MARKET_ODOO_ADAPTER_TOKEN");
        var prevShared = Environment.GetEnvironmentVariable("CIRCLES_SERVICE_KEY");

        try
        {
            Environment.SetEnvironmentVariable("MARKET_ODOO_ADAPTER_PORT", "65001");
            Environment.SetEnvironmentVariable("MARKET_ODOO_ADAPTER_TOKEN", "odoo-secret");
            Environment.SetEnvironmentVariable("CIRCLES_SERVICE_KEY", "shared-secret");

            var p = new EnvOutboundServiceAuthProvider(NullLogger<EnvOutboundServiceAuthProvider>.Instance);

            var uri = new Uri("http://market-adapter-odoo:65001/inventory/100/0xseller/sku");
            var header = await p.TryGetHeaderAsync(uri, "inventory", "0xseller", 100);

            Assert.That(header, Is.Not.Null);
            Assert.That(header!.Value.headerName, Is.EqualTo("X-Circles-Service-Key"));
            Assert.That(header!.Value.apiKey, Is.EqualTo("odoo-secret"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MARKET_ODOO_ADAPTER_PORT", prevPort);
            Environment.SetEnvironmentVariable("MARKET_ODOO_ADAPTER_TOKEN", prevToken);
            Environment.SetEnvironmentVariable("CIRCLES_SERVICE_KEY", prevShared);
        }
    }

    [Test]
    public async Task TryGetHeaderAsync_ReturnsShared_When_Token_Missing()
    {
        var prevPort = Environment.GetEnvironmentVariable("MARKET_CODE_DISPENSER_PORT");
        var prevToken = Environment.GetEnvironmentVariable("MARKET_CODE_DISPENSER_TOKEN");
        var prevShared = Environment.GetEnvironmentVariable("CIRCLES_SERVICE_KEY");

        try
        {
            Environment.SetEnvironmentVariable("MARKET_CODE_DISPENSER_PORT", "65002");
            Environment.SetEnvironmentVariable("MARKET_CODE_DISPENSER_TOKEN", null);
            Environment.SetEnvironmentVariable("CIRCLES_SERVICE_KEY", "shared-secret");

            var p = new EnvOutboundServiceAuthProvider(NullLogger<EnvOutboundServiceAuthProvider>.Instance);

            var uri = new Uri("http://market-adapter-codedispenser:65002/fulfill/100/0xseller");
            var header = await p.TryGetHeaderAsync(uri, "fulfillment", "0xseller", 100);

            Assert.That(header, Is.Not.Null);
            Assert.That(header!.Value.apiKey, Is.EqualTo("shared-secret"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MARKET_CODE_DISPENSER_PORT", prevPort);
            Environment.SetEnvironmentVariable("MARKET_CODE_DISPENSER_TOKEN", prevToken);
            Environment.SetEnvironmentVariable("CIRCLES_SERVICE_KEY", prevShared);
        }
    }
}
