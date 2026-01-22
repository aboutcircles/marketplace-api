using System.Security.Cryptography;
using System.Text;

namespace Circles.Market.Api.Cart;

internal static class BasketHashing
{
    public static string ItemsHash(Basket basket)
    {
        var sb = new StringBuilder();
        foreach (var it in basket.Items
                     .OrderBy(i => i.Seller ?? string.Empty)
                     .ThenBy(i => i.OrderedItem?.Sku ?? string.Empty))
        {
            sb.Append(it.Seller?.Trim()).Append('|')
              .Append(it.OrderedItem?.Sku?.Trim()).Append('|')
              .Append(it.OrderQuantity).Append(';');
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}
