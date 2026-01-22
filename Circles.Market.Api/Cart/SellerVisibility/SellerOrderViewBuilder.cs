using System.Text.Json;

namespace Circles.Market.Api.Cart.SellerVisibility;

/*
This is the ONLY allowed path to produce seller-visible order data.
Pipeline (see also SellerOrderVisibility.md):
  - Load internal OrderSnapshot (unsafe for sellers)
  - Load authoritative line indices from projection (order_line_sellers)
  - Feed both into SellerOrderViewBuilder.Build → SellerOrderDto
The builder enforces index validation and filters both AcceptedOffer and OrderedItem by the same indices.
*/
public static class SellerOrderViewBuilder
{
    public static SellerOrderDto Build(OrderSnapshot internalOrder, IReadOnlyList<int> sellerLineIndices)
    {
        if (internalOrder is null) throw new ArgumentNullException(nameof(internalOrder));
        if (sellerLineIndices is null) throw new ArgumentNullException(nameof(sellerLineIndices));

        // If no indices, treat as unauthorized/not found upstream; builder will also guard.
        if (sellerLineIndices.Count == 0)
            throw new InvalidOperationException("Seller has no line indices in this order");

        int offers = internalOrder.AcceptedOffer?.Count ?? 0;
        int items = internalOrder.OrderedItem?.Count ?? 0;
        if (offers != items)
        {
            throw new InvalidOperationException("Order snapshot is malformed: AcceptedOffer and OrderedItem counts differ");
        }

        // Validate all indices are in range
        foreach (var idx in sellerLineIndices)
        {
            if (idx < 0 || idx >= offers)
                throw new IndexOutOfRangeException($"Seller line index {idx} is out of range (0..{offers - 1})");
        }

        var dto = new SellerOrderDto
        {
            OrderNumber = internalOrder.OrderNumber,
            OrderStatus = internalOrder.OrderStatus,
            OrderDate = internalOrder.OrderDate,
            PaymentReference = internalOrder.PaymentReference,
            Broker = internalOrder.Broker,
            TotalPaymentDue = null, // conservative – do not expose cross-seller totals
            Outbox = new List<OrderOutboxItemDto>() // default empty for seller
        };

        // Filter arrays by the same index set
        foreach (var i in sellerLineIndices)
        {
            dto.AcceptedOffer.Add(internalOrder.AcceptedOffer![i]);
            dto.OrderedItem.Add(internalOrder.OrderedItem![i]);
        }

        // Optional: recompute subtotal if price and quantities exist
        decimal sum = 0m;
        string? currency = null;
        for (int j = 0; j < dto.AcceptedOffer.Count; j++)
        {
            var offer = dto.AcceptedOffer[j];
            var line = dto.OrderedItem[j];
            // After PatchBasket/Canonicalizer enforcement, OrderQuantity must be >= 1
            if (offer.Price is not null)
            {
                sum += (offer.Price.Value * Math.Max(1, line.OrderQuantity));
                currency ??= offer.PriceCurrency;
            }
        }
        if (sum > 0m)
        {
            dto.TotalPaymentDue = new PriceSpecification { Price = sum, PriceCurrency = currency };
        }

        return dto;
    }
}
