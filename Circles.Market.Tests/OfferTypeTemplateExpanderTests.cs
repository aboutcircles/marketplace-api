using Circles.Market.Api.Routing;

namespace Circles.Market.Tests;

[TestFixture]
public class OfferTypeTemplateExpanderTests
{
    [Test]
    public void TryExpand_Replaces_Variables_CaseInsensitive()
    {
        Environment.SetEnvironmentVariable("MARKET_ODOO_ADAPTER_PORT", "65002");

        var ok = OfferTypeTemplateExpander.TryExpand(
            "http://market-adapter-odoo:{market_odoo_adapter_port}/inventory/{CHAIN_ID}/{SeLlEr}/{sKu}",
            100,
            "0xFB406c131101F94182CE69dDd8EB139E172c96Dd",
            "BP1523909",
            out var expanded,
            out var err);

        Assert.That(ok, Is.True, err);
        Assert.That(expanded, Is.EqualTo("http://market-adapter-odoo:65002/inventory/100/0xfb406c131101f94182ce69ddd8eb139e172c96dd/bp1523909"));
    }

    [Test]
    public void TryExpand_Fails_On_Unknown_Variable()
    {
        var ok = OfferTypeTemplateExpander.TryExpand(
            "http://x:{NOPE}/inventory/{chain_id}/{seller}/{sku}",
            100,
            "0xabc",
            "sku",
            out var expanded,
            out var err);

        Assert.That(ok, Is.False);
        Assert.That(expanded, Is.Null);
        Assert.That(err, Does.Contain("Unknown template variable"));
    }
}
