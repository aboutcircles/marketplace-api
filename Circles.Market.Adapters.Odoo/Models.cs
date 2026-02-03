using System.Text.Json;
using System.Text.Json.Serialization;

namespace Circles.Market.Adapters.Odoo;

/// <summary>
/// Converts Odoo's "false" boolean to null for string properties.
/// Odoo returns false instead of null for empty optional string fields like barcode.
/// </summary>
public class OdooFalseAsNullStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.False)
            return null;
        if (reader.TokenType == JsonTokenType.Null)
            return null;
        return reader.GetString();
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value);
    }
}


/// <summary>
/// JSON‑RPC request envelope used by Odoo.
/// </summary>
public class OdooJsonRpcRequest<T>
{
    public string Jsonrpc { get; set; } = "2.0";
    public string Method  { get; set; } = "call";
    public int Id { get; set; } = 1;
    public T Params { get; set; } = default!;
}

/// <summary>
/// JSON‑RPC response envelope returned by Odoo.
/// </summary>
public class OdooJsonRpcResponse<T>
{
    public string Jsonrpc { get; set; } = default!;
    public int Id { get; set; }
    public T Result { get; set; } = default!;
    public OdooJsonRpcError? Error { get; set; }
}

public class OdooJsonRpcError
{
    /// <summary>
    /// Machine-readable error code returned by Odoo for a JSON-RPC call.
    /// </summary>
    public int Code { get; set; }
    /// <summary>
    /// Human-readable error message.
    /// </summary>
    public string Message { get; set; } = default!;
    /// <summary>
    /// Optional structured error details provided by Odoo.
    /// </summary>
    public object? Data { get; set; }
}

/// <summary>
/// The “params” part of the `object.execute_kw` JSON‑RPC call.
/// </summary>
public class OdooExecuteKwParams
{
    public string Service { get; set; } = "object";
    public string Method { get; set; } = "execute_kw";
    public object[] Args { get; set; } = Array.Empty<object>();
}

/// <summary>
/// Minimal partner fields used by the demo endpoints.
/// </summary>
public class PartnerDto
{
    public int Id { get; set; }
    public string Name { get; set; } = default!;
    public string Email { get; set; } = default!;
    public string Phone { get; set; } = default!;
    public string Mobile { get; set; } = default!;
    public string Street { get; set; } = default!;
    public string City { get; set; } = default!;
    public string Zip { get; set; } = default!;
    public string Country { get; set; } = default!;
    public string Vat { get; set; } = default!;
}

/// <summary>
/// Minimal product fields used by the demo endpoints.
/// </summary>
public class ProductDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = default!;

    [JsonPropertyName("default_code")]
    public string DefaultCode { get; set; } = default!;

    [JsonPropertyName("description_sale")]
    public string DescriptionSale { get; set; } = default!;

    [JsonPropertyName("list_price")]
    public decimal ListPrice { get; set; }

    [JsonPropertyName("standard_price")]
    public decimal StandardPrice { get; set; }

    [JsonPropertyName("qty_available")]
    public decimal QtyAvailable { get; set; }
    public string Uom { get; set; } = default!;
}

/// <summary>
/// Minimal fields required to create a sale order (header + lines).
/// </summary>
public class SaleOrderCreateDto
{
    public int PartnerId { get; set; }
    public List<SaleOrderLineDto> Lines { get; set; } = new();
}

public class SaleOrderLineDto
{
    /// <summary>
    /// Odoo internal product identifier.
    /// </summary>
    public int ProductId { get; set; }
    /// <summary>
    /// Quantity to sell.
    /// </summary>
    public decimal Quantity { get; set; }
    /// <summary>
    /// Unit price for the product (currency resolved by Odoo).
    /// </summary>
    public decimal? UnitPrice { get; set; }
}

public class OdooProductVariantMinimalDto
{
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public int Id { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("default_code")]
    public string? DefaultCode { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }
}

/// <summary>
/// Product variant listing fields for admin catalog lookups.
/// </summary>
public class OdooProductVariantListItemDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("default_code")]
    public string? DefaultCode { get; set; }

    // Odoo many2one: [id, "Name"]
    [JsonPropertyName("product_tmpl_id")]
    public JsonElement ProductTemplateRaw { get; set; }

    [JsonPropertyName("barcode")]
    [JsonConverter(typeof(OdooFalseAsNullStringConverter))]
    public string? Barcode { get; set; }

    [JsonPropertyName("qty_available")]
    public decimal QtyAvailable { get; set; }

    [JsonPropertyName("active")]
    public bool Active { get; set; }
}

public class OdooSaleOrderReadDto
{
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public int Id { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public string? Name { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("state")]
    public string? State { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("amount_total")]
    public decimal? AmountTotal { get; set; }
}

/// <summary>
/// Minimal stock information for a product template queried from Odoo.
/// </summary>
public class OdooProductTemplateStockDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = default!;

    [JsonPropertyName("qty_available")]
    public decimal QtyAvailable { get; set; }
}

/// <summary>
/// Minimal identity of a product template (id, display name, default code/SKU).
/// </summary>
public class OdooProductTemplateMinimalDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = default!;

    [JsonPropertyName("default_code")]
    public string DefaultCode { get; set; } = default!;
}

/// <summary>
/// Minimal identity of a partner/contact in Odoo.
/// </summary>
public class OdooPartnerMinimalDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = default!;
}

public class OperationDetailsDto
{
    public int Id { get; set; }
    public int? CarrierId { get; set; }
    public string? CarrierTrackingRef { get; set; }
}

/// <summary>
/// Information about a stock picking (shipment) including carrier details.
/// </summary>
public class OdooStockPickingInfoDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("carrier_tracking_ref")]
    public string? CarrierTrackingRef { get; set; }

    // Odoo sends many2one as [id, "Name"], so we keep the raw JsonElement
    [JsonPropertyName("carrier_id")]
    public JsonElement CarrierRaw { get; set; }

    /// <summary>
    /// Extracted carrier id from the many2one raw value when available.
    /// </summary>
    [JsonIgnore]
    public int? CarrierId
    {
        get
        {
            bool isArray = CarrierRaw.ValueKind == JsonValueKind.Array;
            bool hasAtLeastOneElement = isArray && CarrierRaw.GetArrayLength() >= 1;

            if (hasAtLeastOneElement)
            {
                JsonElement firstElement = CarrierRaw[0];

                bool isNumber = firstElement.ValueKind == JsonValueKind.Number;
                int idValue = default;
                bool parsedId = isNumber && firstElement.TryGetInt32(out idValue);

                if (parsedId)
                {
                    return idValue;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Extracted carrier display name from the many2one raw value when available.
    /// </summary>
    [JsonIgnore]
    public string? CarrierName
    {
        get
        {
            bool isArray = CarrierRaw.ValueKind == JsonValueKind.Array;
            bool hasAtLeastTwoElements = isArray && CarrierRaw.GetArrayLength() >= 2;

            if (hasAtLeastTwoElements)
            {
                JsonElement secondElement = CarrierRaw[1];
                bool isString = secondElement.ValueKind == JsonValueKind.String;

                if (isString)
                {
                    string? nameValue = secondElement.GetString();
                    return nameValue;
                }
            }

            return null;
        }
    }
}
