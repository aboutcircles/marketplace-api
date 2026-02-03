using Circles.Market.Api.Cart;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Circles.Market.Tests;

[TestFixture]
public class OrderAccessServiceSanitizationTests
{
    [Test]
    public async Task GetOrderForBuyerAsync_Nulls_FulfillmentEndpoint()
    {
        var store = new Mock<IOrderStore>(MockBehavior.Strict);

        var orderId = "ord_123";
        var buyer = "0xbuyer";
        var chainId = 100L;

        store.Setup(x => x.GetOwnerByOrderId(orderId))
            .Returns((buyer, chainId));

        var snapshot = new OrderSnapshot
        {
            OrderNumber = orderId,
            Customer = new SchemaOrgPersonId { Id = $"eip155:{chainId}:{buyer}" },
            AcceptedOffer = new List<OfferSnapshot>
            {
                new OfferSnapshot
                {
                    CirclesFulfillmentEndpoint = "https://evil.example/fulfill",
                    Price = 1m,
                    PriceCurrency = "CRC",
                    Seller = new SchemaOrgOrgId { Id = "eip155:100:0xseller" }
                }
            },
            OrderedItem = new List<OrderItemLine> { new OrderItemLine { OrderQuantity = 1, OrderedItem = new OrderedItemRef { Sku = "sku" } } }
        };

        store.Setup(x => x.Get(orderId)).Returns(snapshot);

        var svc = new OrderAccessService(store.Object, NullLogger<OrderAccessService>.Instance);

        var result = await svc.GetOrderForBuyerAsync(orderId, buyer, chainId);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.AcceptedOffer[0].CirclesFulfillmentEndpoint, Is.Null);
    }
}
