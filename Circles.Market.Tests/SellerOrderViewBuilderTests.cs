using Circles.Market.Api.Cart;
using Circles.Market.Api.Cart.SellerVisibility;

namespace Circles.Market.Tests;

[TestFixture]
public class SellerOrderViewBuilderTests
{
    private static OrderSnapshot MakeSampleOrder()
    {
        var o = new OrderSnapshot
        {
            OrderNumber = "ord_ABC",
            OrderStatus = "https://schema.org/OrderPaymentDue",
            OrderDate = "2024-01-01T00:00:00Z",
            PaymentReference = "pay_deadbeef",
            Broker = new SchemaOrgOrgId { Type = "Organization", Id = "eip155:100:0xbroker" },
            AcceptedOffer = new List<OfferSnapshot>
            {
                new OfferSnapshot { Price = 1.5m, PriceCurrency = "CRC", Seller = new SchemaOrgOrgId{ Type="Organization", Id = "eip155:100:0xsellerA" } },
                new OfferSnapshot { Price = 2.0m, PriceCurrency = "CRC", Seller = new SchemaOrgOrgId{ Type="Organization", Id = "eip155:100:0xsellerB" } },
                new OfferSnapshot { Price = 3.0m, PriceCurrency = "CRC", Seller = new SchemaOrgOrgId{ Type="Organization", Id = "eip155:100:0xsellerA" } },
            },
            OrderedItem = new List<OrderItemLine>
            {
                new OrderItemLine { OrderQuantity = 2, OrderedItem = new OrderedItemRef{ Sku = "A1" }, ProductCid = "cid1" },
                new OrderItemLine { OrderQuantity = 1, OrderedItem = new OrderedItemRef{ Sku = "B1" }, ProductCid = "cid2" },
                new OrderItemLine { OrderQuantity = 3, OrderedItem = new OrderedItemRef{ Sku = "A2" }, ProductCid = "cid3" },
            }
        };
        return o;
    }

    [Test]
    public void Build_Filters_MultiSeller_NoLeak()
    {
        var order = MakeSampleOrder();
        // seller A owns lines 0 and 2
        var indices = new[] { 0, 2 };
        var dto = SellerOrderViewBuilder.Build(order, indices);

        Assert.That(dto.AcceptedOffer.Count, Is.EqualTo(2));
        Assert.That(dto.OrderedItem.Count, Is.EqualTo(2));
        // Ensure no seller B appears
        Assert.That(dto.AcceptedOffer.Any(a => a.Seller?.Id?.Contains("0xsellerB") == true), Is.False);
        // Subtotal: (1.5*2) + (3.0*3) = 3 + 9 = 12
        Assert.That(dto.TotalPaymentDue?.Price, Is.EqualTo(12m));
        Assert.That(dto.TotalPaymentDue?.PriceCurrency, Is.EqualTo("CRC"));
    }

    [Test]
    public void Build_Throws_On_Index_Mismatch()
    {
        var order = MakeSampleOrder();
        // break parity deliberately
        order.OrderedItem.RemoveAt(2);
        Assert.Throws<InvalidOperationException>(() => SellerOrderViewBuilder.Build(order, new[] { 0 }));
    }
}
