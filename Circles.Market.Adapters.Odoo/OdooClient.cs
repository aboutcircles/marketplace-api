using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Circles.Market.Adapters.Odoo;

/// <summary>
/// Thin wrapper around Odoo’s JSON-RPC API.
/// You can register this with DI and inject it into ASP.NET handlers.
/// </summary>
public class OdooClient
{
    public const string JsonRpcVersion = "2.0";
    public const string JsonRpcMethodCall = "call";
    public const string ServiceObject = "object";
    public const string MethodExecuteKw = "execute_kw";

    private readonly HttpClient _http;
    private readonly OdooSettings _settings;
    private readonly ILogger<OdooClient> _logger;

    public OdooClient(HttpClient http, OdooSettings settings, ILogger<OdooClient> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (_http.BaseAddress == null)
        {
            _http.BaseAddress = new Uri(_settings.JsonRpcUrl);
        }
    }

    public OdooSettings GetSettings() => _settings;

    public Task UpdateBaseAddressAsync(CancellationToken ct)
    {
        _http.BaseAddress = new Uri(_settings.JsonRpcUrl);
        return Task.CompletedTask;
    }

    private async Task<T> ExecuteKwAsync<T>(
        string model,
        string method,
        object[] positionalArgs,
        Dictionary<string, object>? keywordArgs = null,
        CancellationToken cancellationToken = default)
    {
        bool isModelMissing = string.IsNullOrWhiteSpace(model);
        if (isModelMissing)
        {
            throw new ArgumentException("Model must be provided.", nameof(model));
        }

        bool isMethodMissing = string.IsNullOrWhiteSpace(method);
        if (isMethodMissing)
        {
            throw new ArgumentException("Method must be provided.", nameof(method));
        }

        Dictionary<string, object> effectiveKeywordArgs = keywordArgs ?? new Dictionary<string, object>();

        object[] args = new object[]
        {
            _settings.Db,
            _settings.UserId,
            _settings.Key,
            model,
            method,
            positionalArgs,
            effectiveKeywordArgs
        };

        var rpcParams = new OdooExecuteKwParams
        {
            Service = ServiceObject,
            Method = MethodExecuteKw,
            Args = args
        };

        var request = new OdooJsonRpcRequest<OdooExecuteKwParams>
        {
            Jsonrpc = JsonRpcVersion,
            Method = JsonRpcMethodCall,
            Id = 1,
            Params = rpcParams
        };

        // Logging: do not log raw args to avoid leaking credentials.
        _logger.LogInformation(
            "Calling Odoo execute_kw: model={Model} method={Method}",
            model, method);

        var sw = Stopwatch.StartNew();
        try
        {
            using HttpResponseMessage response = await _http.PostAsJsonAsync(string.Empty, request, cancellationToken);
            response.EnsureSuccessStatusCode();

            OdooJsonRpcResponse<T>? rpcResponse;
            try
            {
                rpcResponse = await response.Content.ReadFromJsonAsync<OdooJsonRpcResponse<T>>(cancellationToken: cancellationToken);
            }
            catch (JsonException ex)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError(ex, "Failed to deserialize Odoo JSON-RPC response. Body: {Body}", body);
                throw;
            }

            if (rpcResponse == null)
            {
                _logger.LogError("Empty JSON-RPC response from Odoo: model={Model} method={Method}", model, method);
                throw new InvalidOperationException("Odoo returned an empty JSON-RPC response.");
            }

            if (rpcResponse.Error != null)
            {
                var error = rpcResponse.Error;
                string? dataDetail = null;
                if (error.Data is JsonElement element)
                {
                    dataDetail = element.GetRawText();
                }
                else if (error.Data != null)
                {
                    dataDetail = error.Data.ToString();
                }

                _logger.LogError(
                    "Odoo JSON-RPC error: model={Model} method={Method} code={Code} message={Message} data={Data}",
                    model, method, error.Code, error.Message, dataDetail ?? (object?)error.Data);

                string exceptionMessage = $"Odoo error {error.Code}: {error.Message}";
                if (!string.IsNullOrWhiteSpace(dataDetail))
                {
                    exceptionMessage += $". Data: {dataDetail}";
                }

                throw new InvalidOperationException(exceptionMessage);
            }

            sw.Stop();
            _logger.LogInformation(
                "Odoo execute_kw succeeded: model={Model} method={Method} durationMs={Duration}",
                model, method, sw.ElapsedMilliseconds);

            return rpcResponse.Result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            _logger.LogError(ex,
                "Odoo execute_kw failed: model={Model} method={Method} durationMs={Duration}",
                model, method, sw.ElapsedMilliseconds);
            throw;
        }
    }

    // ---------------------------------------------------------------------
    // Generic search_read
    // ---------------------------------------------------------------------
    public Task<T[]> SearchReadAsync<T>(
        string model,
        object[] domain,
        string[] fields,
        int? limit = null,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        bool isDomainNull = domain == null;
        if (isDomainNull)
        {
            throw new ArgumentNullException(nameof(domain));
        }

        bool areFieldsNull = fields == null;
        if (areFieldsNull)
        {
            throw new ArgumentNullException(nameof(fields));
        }

        object[] positionalArgs = new object[]
        {
            domain!
        };

        var keywordArgs = new Dictionary<string, object>
        {
            ["fields"] = fields!,
            ["offset"] = offset
        };

        if (limit.HasValue)
        {
            keywordArgs["limit"] = limit.Value;
        }

        _logger.LogDebug(
            "Odoo search_read: model={Model} fields={FieldsCount} limit={Limit} offset={Offset}",
            model, fields!.Length, (object?)limit, offset);

        return ExecuteKwAsync<T[]>(model, "search_read", positionalArgs, keywordArgs, cancellationToken);
    }

    // ---------------------------------------------------------------------
    // Generic helpers kept from your mock (read by id, stock by ids)
    // ---------------------------------------------------------------------
    public async Task<PartnerDto?> GetPartnerAsync(int partnerId, CancellationToken cancellationToken = default)
    {
        object[] positionalArgs = new object[]
        {
            new[] { partnerId }
        };

        var keywordArgs = new Dictionary<string, object>
        {
            ["fields"] = new[]
            {
                "id", "name", "email", "phone", "mobile", "street", "city", "zip", "vat"
            }
        };

        _logger.LogDebug("GetPartnerAsync: partnerId={PartnerId}", partnerId);

        PartnerDto[] partners =
            await ExecuteKwAsync<PartnerDto[]>("res.partner", "read", positionalArgs, keywordArgs, cancellationToken);

        if (partners == null || partners.Length == 0)
        {
            _logger.LogWarning("GetPartnerAsync: partnerId={PartnerId} not found", partnerId);
            return null;
        }

        PartnerDto partner = partners[0];
        return partner;
    }

    public async Task<ProductDto?> GetProductAsync(int productId, CancellationToken cancellationToken = default)
    {
        object[] positionalArgs = new object[]
        {
            new[] { productId }
        };

        var keywordArgs = new Dictionary<string, object>
        {
            ["fields"] = new[]
            {
                "id",
                "name",
                "default_code",
                "description_sale",
                "list_price",
                "standard_price",
                "qty_available"
            }
        };

        _logger.LogDebug("GetProductAsync: productId={ProductId}", productId);

        ProductDto[] products =
            await ExecuteKwAsync<ProductDto[]>("product.product", "read", positionalArgs, keywordArgs,
                cancellationToken);

        if (products == null || products.Length == 0)
        {
            _logger.LogWarning("GetProductAsync: productId={ProductId} not found", productId);
            return null;
        }

        ProductDto product = products[0];
        return product;
    }

    public async Task<Dictionary<int, decimal>> GetStockAsync(int[] productIds,
        CancellationToken cancellationToken = default)
    {
        bool areProductIdsNull = productIds == null;
        if (areProductIdsNull)
        {
            throw new ArgumentNullException(nameof(productIds));
        }

        object[] positionalArgs = new object[]
        {
            productIds!
        };

        var keywordArgs = new Dictionary<string, object>
        {
            ["fields"] = new[] { "id", "qty_available" }
        };

        _logger.LogDebug("GetStockAsync: productCount={Count}", productIds!.Length);

        ProductDto[] products =
            await ExecuteKwAsync<ProductDto[]>("product.product", "read", positionalArgs, keywordArgs,
                cancellationToken);

        if (products == null)
        {
            _logger.LogError("GetStockAsync: null products response for {Count} ids", productIds.Length);
            throw new InvalidOperationException("Odoo returned null when reading product stock.");
        }

        return products.ToDictionary(p => p.Id, p => p.QtyAvailable);
    }

    public async Task<int> CreateSaleOrderAsync(SaleOrderCreateDto order, CancellationToken cancellationToken = default)
    {
        if (order == null) throw new ArgumentNullException(nameof(order));
        if (order.PartnerId <= 0) throw new ArgumentOutOfRangeException(nameof(order.PartnerId));
        if (order.Lines == null || order.Lines.Count == 0) throw new ArgumentException("No order lines", nameof(order));

        object[] orderLines = order.Lines
            .Select(line =>
            {
                var vals = new Dictionary<string, object>
                {
                    ["product_id"] = line.ProductId,
                    ["product_uom_qty"] = line.Quantity
                };

                if (line.UnitPrice.HasValue)
                {
                    vals["price_unit"] = line.UnitPrice.Value;
                }

                return new object[] { 0, 0, vals };
            })
            .ToArray();

        var createVals = new Dictionary<string, object>
        {
            ["partner_id"] = order.PartnerId,
            ["partner_invoice_id"] = order.PartnerId,
            ["partner_shipping_id"] = order.PartnerId,
            ["order_line"] = orderLines
        };

        // Important: pass a single dict as the method argument (not a list-of-dicts)
        object[] positionalArgs = new object[] { createVals };

        JsonElement result = await ExecuteKwAsync<JsonElement>(
            model: "sale.order",
            method: "create",
            positionalArgs: positionalArgs,
            keywordArgs: null,
            cancellationToken: cancellationToken
        );

        return ParseOdooCreateResultToId(result);
    }

    private static int ParseOdooCreateResultToId(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Number)
        {
            return result.GetInt32();
        }

        if (result.ValueKind == JsonValueKind.Array)
        {
            int len = result.GetArrayLength();
            if (len == 1)
            {
                var first = result[0];
                if (first.ValueKind == JsonValueKind.Number)
                {
                    return first.GetInt32();
                }
            }
        }

        throw new InvalidOperationException($"Unexpected sale.order.create result kind={result.ValueKind}: {result}");
    }

    // ---------------------------------------------------------------------
    // High-level methods that mirror your Postman calls
    // ---------------------------------------------------------------------

    public async Task<OdooProductTemplateStockDto?> GetProductStockByCodeAsync(
        string defaultCode,
        CancellationToken cancellationToken = default)
    {
        bool isDefaultCodeMissing = string.IsNullOrWhiteSpace(defaultCode);
        if (isDefaultCodeMissing)
        {
            throw new ArgumentException("Default code must be provided.", nameof(defaultCode));
        }

        _logger.LogInformation("GetProductStockByCodeAsync: defaultCode={DefaultCode}", defaultCode);

        object[] domain = new object[]
        {
            new object[] { "default_code", "=", defaultCode }
        };

        string[] fields = new[]
        {
            "id",
            "display_name",
            "qty_available"
        };

        OdooProductTemplateStockDto[] records =
            await SearchReadAsync<OdooProductTemplateStockDto>(
                "product.template",
                domain!,
                fields!,
                limit: 1,
                offset: 0,
                cancellationToken: cancellationToken);

        if (records == null || records.Length == 0)
        {
            _logger.LogWarning("GetProductStockByCodeAsync: defaultCode={DefaultCode} not found", defaultCode!);
            return null;
        }

        return records[0];
    }

    public async Task<OdooProductTemplateMinimalDto?> GetProductTemplateByCodeAsync(
        string defaultCode,
        CancellationToken cancellationToken = default)
    {
        bool isDefaultCodeMissing = string.IsNullOrWhiteSpace(defaultCode);
        if (isDefaultCodeMissing)
        {
            throw new ArgumentException("Default code must be provided.", nameof(defaultCode));
        }

        _logger.LogInformation("GetProductTemplateByCodeAsync: defaultCode={DefaultCode}", defaultCode);

        object[] domain = new object[]
        {
            new object[] { "default_code", "=", defaultCode }
        };

        string[] fields = new[]
        {
            "id",
            "display_name",
            "default_code"
        };

        OdooProductTemplateMinimalDto[] records =
            await SearchReadAsync<OdooProductTemplateMinimalDto>(
                "product.template",
                domain!,
                fields!,
                limit: 1,
                offset: 0,
                cancellationToken: cancellationToken);

        if (records == null || records.Length == 0)
        {
            _logger.LogWarning("GetProductTemplateByCodeAsync: defaultCode={DefaultCode} not found", defaultCode!);
            return null;
        }

        return records[0];
    }

    public async Task<OdooPartnerMinimalDto?> GetCustomerByNameAsync(
        string nameFragment,
        CancellationToken cancellationToken = default)
    {
        bool isNameFragmentMissing = string.IsNullOrWhiteSpace(nameFragment);
        if (isNameFragmentMissing)
        {
            throw new ArgumentException("Name fragment must be provided.", nameof(nameFragment));
        }

        _logger.LogInformation("GetCustomerByNameAsync: nameFragment={NameFragment}", nameFragment);

        string likePattern = $"%{nameFragment}%";

        object[] domain = new object[]
        {
            new object[] { "name", "ilike", likePattern }
        };

        string[] fields = new[]
        {
            "id",
            "name"
        };

        OdooPartnerMinimalDto[] records =
            await SearchReadAsync<OdooPartnerMinimalDto>(
                "res.partner",
                domain!,
                fields!,
                limit: 1,
                offset: 0,
                cancellationToken: cancellationToken);

        if (records == null || records.Length == 0)
        {
            _logger.LogWarning("GetCustomerByNameAsync: no match for fragment={NameFragment}", nameFragment!);
            return null;
        }

        return records[0];
    }

    public async Task<OperationDetailsDto?> GetOperationDetailsByOriginAsync(string origin, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return null;
        }

        string o = origin.Trim();
        _logger.LogInformation("GetOperationDetailsByOriginAsync: origin={Origin}", o);

        object[] domain = new object[]
        {
            new object[] { "origin", "=", o }
        };

        var kwargs = new Dictionary<string, object>
        {
            ["fields"] = new[] { "id", "carrier_id", "carrier_tracking_ref" },
            ["limit"] = 1
        };

        // Use JsonElement so we can tolerate Odoo 'false' values.
        var result = await ExecuteKwAsync<System.Text.Json.JsonElement>(
            model: "stock.picking",
            method: "search_read",
            positionalArgs: new object[] { domain },
            keywordArgs: kwargs,
            cancellationToken: cancellationToken
        );

        if (result.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return null;
        }

        if (result.GetArrayLength() == 0)
        {
            return null;
        }

        var first = result[0];
        if (first.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return null;
        }

        int id = first.TryGetProperty("id", out var idEl) && idEl.ValueKind == System.Text.Json.JsonValueKind.Number
            ? idEl.GetInt32()
            : 0;

        int? carrierId = null;
        if (first.TryGetProperty("carrier_id", out var carrierEl))
        {
            // Many2one fields can be: false OR [id, name]
            if (carrierEl.ValueKind == System.Text.Json.JsonValueKind.Array && carrierEl.GetArrayLength() > 0)
            {
                var carrierIdEl = carrierEl[0];
                if (carrierIdEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    carrierId = carrierIdEl.GetInt32();
                }
            }
        }

        string? trackingRef = null;
        if (first.TryGetProperty("carrier_tracking_ref", out var trackEl))
        {
            if (trackEl.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                trackingRef = trackEl.GetString();
            }
            // if it's False/Null, leave as null
        }

        return new OperationDetailsDto
        {
            Id = id,
            CarrierId = carrierId,
            CarrierTrackingRef = trackingRef
        };
    }

    public async Task<int> ResolveProductVariantIdByCodeAsync(string defaultCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(defaultCode))
        {
            throw new ArgumentException("Default code must be provided.", nameof(defaultCode));
        }

        string code = defaultCode.Trim();

        object[] domain = new object[]
        {
            new object[] { "default_code", "=", code }
        };

        string[] fields = new[] { "id", "display_name", "default_code" };

        var records = await SearchReadAsync<OdooProductVariantMinimalDto>(
            "product.product",
            domain,
            fields,
            limit: 1,
            offset: 0,
            cancellationToken: cancellationToken);

        if (records == null || records.Length == 0)
        {
            throw new InvalidOperationException($"No product.product found for default_code={code}");
        }

        return records[0].Id;
    }

    public async Task<OdooSaleOrderReadDto> ReadSaleOrderAsync(int orderId, CancellationToken cancellationToken = default)
    {
        if (orderId <= 0) throw new ArgumentOutOfRangeException(nameof(orderId));

        object[] positionalArgs = new object[] { new[] { orderId } };
        var keywordArgs = new Dictionary<string, object>
        {
            ["fields"] = new[] { "id", "name", "state", "amount_total" }
        };

        var res = await ExecuteKwAsync<OdooSaleOrderReadDto[]>(
            "sale.order",
            "read",
            positionalArgs,
            keywordArgs,
            cancellationToken);

        if (res == null || res.Length == 0)
        {
            throw new InvalidOperationException($"sale.order.read returned no rows for id={orderId}");
        }

        return res[0];
    }

    public async Task ConfirmSaleOrderAsync(int orderId, CancellationToken cancellationToken = default)
    {
        if (orderId <= 0) throw new ArgumentOutOfRangeException(nameof(orderId));

        object[] positionalArgs = new object[] { new[] { orderId } };
        await ExecuteKwAsync<object>(
            "sale.order",
            "action_confirm",
            positionalArgs,
            null,
            cancellationToken);
    }
}
