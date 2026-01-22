namespace Circles.Market.Adapters.Odoo;

public class OdooSettings
{
    public string Db { get; set; } = default!;
    public int UserId { get; set; }                 // uid
    public string Key { get; set; } = default!;
    public string BaseUrl { get; set; } = default!;
    public string JsonRpcUrl => $"{BaseUrl.TrimEnd('/')}/jsonrpc";

    public int? SalePartnerId { get; set; }         // required for sale.order.create

    public int JsonRpcTimeoutMs { get; set; } = 30000;
    public bool FulfillInheritRequestAbort { get; set; } = false;
}