using System.Text.Json;
using Npgsql;

namespace Circles.Market.Api.Cart;

public interface IOrderStore
{
    // Persist a finalized order snapshot. Returns true if created, false if already exists.
    bool Create(string orderId, string basketId, OrderSnapshot order);

    // Retrieve a stored order snapshot (INTERNAL/UNSAFE for sellers)
    OrderSnapshot? Get(string orderId);

    // INTERNAL: used by seller-safe reads to avoid expensive outbox loads
    OrderSnapshot? GetInternal(string orderId, bool includeOutbox);

    // Retrieve orders for a buyer (address + chain), newest first, paginated
    IEnumerable<OrderSnapshot> GetByBuyer(string buyerAddress, long chainId, int page, int pageSize);

    // Ownership projection by order id (for access control)
    (string? BuyerAddress, long? BuyerChainId)? GetOwnerByOrderId(string orderId);

    // Seller projections / safe pipeline helpers
    IEnumerable<string> GetOrderIdsBySeller(string sellerAddress, long chainId, int page, int pageSize);
    IReadOnlyList<int> GetOrderLineIndicesForSeller(string orderId, string sellerAddress, long chainId);
    bool OrderContainsSeller(string orderId, string sellerAddress, long chainId);

    // Match: mark an order paid by public payment reference (idempotent; returns true if state changed)
    bool TryMarkPaidByReference(
        string paymentReference,
        long paidChainId,
        string txHash,
        int logIndex,
        string gatewayAddress,
        System.Numerics.BigInteger? amountWei,
        DateTimeOffset paidAt);

    // Optional follow-up state transitions (idempotent; return true if state changed)
    bool TryMarkConfirmedByReference(string paymentReference, DateTimeOffset confirmedAt);
    bool TryMarkFinalizedByReference(string paymentReference, DateTimeOffset finalizedAt);

    // NEW: retrieve status history rows (old/new status + timestamp)
    IEnumerable<OrderStatusHistoryEntry> GetStatusHistory(string orderId);

    // Lookup orders by payment reference (used by SSE hooks)
    IEnumerable<(string OrderId, string? BuyerAddress, long? BuyerChainId)> GetByPaymentReference(string paymentReference);

    // Outbox: store and retrieve arbitrary JSON-LD payloads per order
    void AddOutboxItem(string orderId, string? source, JsonElement payload);
    IEnumerable<OrderOutboxItem> GetOutboxItems(string orderId);
}

// NEW record type for status history entries
public sealed record OrderStatusHistoryEntry(
    string? OldStatus,
    string NewStatus,
    DateTimeOffset ChangedAt
);

public class PostgresOrderStore : IOrderStore
{
    private readonly string _connString;
    private readonly ILogger<PostgresOrderStore> _logger;

    public PostgresOrderStore(string connString, ILogger<PostgresOrderStore> logger)
    {
        _connString = connString;
        _logger = logger;
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        _logger.LogInformation("EnsureSchema starting for orders...");
        try
        {
            using var conn = new NpgsqlConnection(_connString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS orders (
  order_id    text PRIMARY KEY,
  basket_id   text NOT NULL,
  order_json  jsonb NOT NULL,
  status      text NOT NULL,
  created_at  timestamptz NOT NULL
);";
            cmd.ExecuteNonQuery();

            using var idx = conn.CreateCommand();
            idx.CommandText = "CREATE INDEX IF NOT EXISTS ix_orders_basket ON orders (basket_id);";
            idx.ExecuteNonQuery();

            // New columns for buyer-scoped access
            using var alter = conn.CreateCommand();
            alter.CommandText = @"
ALTER TABLE orders ADD COLUMN IF NOT EXISTS buyer_address text NULL;
ALTER TABLE orders ADD COLUMN IF NOT EXISTS buyer_chain_id bigint NULL;
ALTER TABLE orders ADD COLUMN IF NOT EXISTS payment_reference text NULL;
ALTER TABLE orders ADD COLUMN IF NOT EXISTS paid_at timestamptz NULL;
ALTER TABLE orders ADD COLUMN IF NOT EXISTS confirmed_at timestamptz NULL;
ALTER TABLE orders ADD COLUMN IF NOT EXISTS finalized_at timestamptz NULL;
ALTER TABLE orders ADD COLUMN IF NOT EXISTS paid_tx_hash text NULL;
ALTER TABLE orders ADD COLUMN IF NOT EXISTS paid_log_index integer NULL;
ALTER TABLE orders ADD COLUMN IF NOT EXISTS paid_chain_id bigint NULL;
ALTER TABLE orders ADD COLUMN IF NOT EXISTS paid_gateway text NULL;
ALTER TABLE orders ADD COLUMN IF NOT EXISTS paid_amount_wei numeric(78,0) NULL;
";
            alter.ExecuteNonQuery();

            using var idx2 = conn.CreateCommand();
            idx2.CommandText = "CREATE INDEX IF NOT EXISTS ix_orders_buyer ON orders (buyer_address);";
            idx2.ExecuteNonQuery();

            using var idx3 = conn.CreateCommand();
            idx3.CommandText = "CREATE INDEX IF NOT EXISTS ix_orders_buyer_chain ON orders (buyer_address, buyer_chain_id);";
            idx3.ExecuteNonQuery();

            // Unique payment reference if present
            using var idx4 = conn.CreateCommand();
            idx4.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS ux_orders_payment_reference ON orders (payment_reference) WHERE payment_reference IS NOT NULL;";
            idx4.ExecuteNonQuery();

            // Helpful projections for paid lookups
            using var idx5 = conn.CreateCommand();
            idx5.CommandText = "CREATE INDEX IF NOT EXISTS ix_orders_paid_ref ON orders (payment_reference, paid_at);";
            idx5.ExecuteNonQuery();

            // History table for status transitions
            using (var hist = conn.CreateCommand())
            {
                hist.CommandText = @"
CREATE TABLE IF NOT EXISTS orders_status_history (
  id           bigserial PRIMARY KEY,
  order_id     text NOT NULL,
  old_status   text NULL,
  new_status   text NOT NULL,
  changed_at   timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_orders_status_history_order ON orders_status_history (order_id, changed_at);
";
                hist.ExecuteNonQuery();
            }

            // Trigger function to capture status changes
            using (var fn = conn.CreateCommand())
            {
                fn.CommandText = @"
CREATE OR REPLACE FUNCTION orders_status_history_fn()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF OLD IS NULL THEN
        -- Initial status on insert
        INSERT INTO orders_status_history(order_id, old_status, new_status, changed_at)
        VALUES (NEW.order_id, NULL, NEW.status, now());
    ELSIF NEW.status IS DISTINCT FROM OLD.status THEN
        INSERT INTO orders_status_history(order_id, old_status, new_status, changed_at)
        VALUES (NEW.order_id, OLD.status, NEW.status, now());
    END IF;
    RETURN NEW;
END;
$$;";
                fn.ExecuteNonQuery();
            }

            // Trigger that calls the function on status updates
            using (var trg = conn.CreateCommand())
            {
                trg.CommandText = @"
DROP TRIGGER IF EXISTS orders_status_history_trg ON orders;
CREATE TRIGGER orders_status_history_trg
AFTER INSERT OR UPDATE OF status ON orders
FOR EACH ROW
EXECUTE FUNCTION orders_status_history_fn();";
                trg.ExecuteNonQuery();
            }

            // Outbox table for fulfillment/seller messages
            using (var outbox = conn.CreateCommand())
            {
                outbox.CommandText = @"
CREATE TABLE IF NOT EXISTS order_outbox (
  id          bigserial PRIMARY KEY,
  order_id    text NOT NULL,
  payload     jsonb NOT NULL,
  source      text NULL,
  created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_order_outbox_order
  ON order_outbox (order_id, created_at);";
                outbox.ExecuteNonQuery();
            }

            // Projection tables for seller visibility
            using (var proj = conn.CreateCommand())
            {
                proj.CommandText = @"
CREATE TABLE IF NOT EXISTS order_sellers (
  order_id         text   NOT NULL,
  seller_address   text   NOT NULL,
  seller_chain_id  bigint NOT NULL,
  created_at       timestamptz NOT NULL,
  PRIMARY KEY (order_id, seller_address, seller_chain_id)
);

CREATE INDEX IF NOT EXISTS ix_order_sellers_seller
  ON order_sellers (seller_address, seller_chain_id, created_at DESC);

CREATE TABLE IF NOT EXISTS order_line_sellers (
  order_id         text    NOT NULL,
  line_index       integer NOT NULL,
  seller_address   text    NOT NULL,
  seller_chain_id  bigint  NOT NULL,
  created_at       timestamptz NOT NULL,
  PRIMARY KEY (order_id, line_index, seller_address, seller_chain_id)
);

CREATE INDEX IF NOT EXISTS ix_order_line_sellers_lookup
  ON order_line_sellers (seller_address, seller_chain_id, created_at DESC);

CREATE INDEX IF NOT EXISTS ix_order_line_sellers_order
  ON order_line_sellers (order_id, seller_address, seller_chain_id, line_index);
";
                proj.ExecuteNonQuery();
            }

            // One-off sales tracking table for sold-once default availability
            using (var oneOff = conn.CreateCommand())
            {
                oneOff.CommandText = @"
CREATE TABLE IF NOT EXISTS one_off_sales (
  chain_id       bigint NOT NULL,
  seller_address text   NOT NULL,
  sku            text   NOT NULL,
  order_id       text   NOT NULL,
  ordered_at     timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (chain_id, seller_address, sku)
);

CREATE INDEX IF NOT EXISTS ix_one_off_sales_order_id
  ON one_off_sales(order_id);
";
                oneOff.ExecuteNonQuery();
            }
            _logger.LogInformation("EnsureSchema completed for orders.");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "FATAL: Failed to ensure Postgres schema for orders");
            throw;
        }
    }

    public bool Create(string orderId, string basketId, OrderSnapshot order)
    {
        using var conn = new NpgsqlConnection(_connString);
        conn.Open();

        // Validate index parity for safety: AcceptedOffer and OrderedItem must have same count
        if (order.AcceptedOffer.Count != order.OrderedItem.Count)
        {
            throw new InvalidOperationException("Order snapshot is malformed: AcceptedOffer and OrderedItem counts differ.");
        }

        // Extract buyer address and chain id from the order snapshot if present (customer @id like eip155:<chainId>:<address>)
        string? buyerAddr = null;
        long? buyerChain = null;
        try
        {
            string? id = order.Customer?.Id;
            if (!string.IsNullOrWhiteSpace(id) && id!.StartsWith("eip155:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = id.Split(':', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 3)
                {
                    if (long.TryParse(parts[1], out var ch)) buyerChain = ch;
                    buyerAddr = parts[2].ToLowerInvariant();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse buyer id from order snapshot {OrderId}", orderId);
            // keep buyer nulls; buyer columns are optional
        }

        // Parse seller projections and validate seller ids
        var distinctSellers = new HashSet<(string addr, long chain)>();
        var lineOwners = new List<(int index, string addr, long chain)>();
        for (int i = 0; i < order.AcceptedOffer.Count; i++)
        {
            var offer = order.AcceptedOffer[i];
            var sid = offer.Seller?.Id;
            if (string.IsNullOrWhiteSpace(sid))
            {
                throw new InvalidOperationException($"Order {orderId} line {i} has no seller id");
            }
            var parts = sid.Split(':', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 || !long.TryParse(parts[1], out var ch))
            {
                throw new InvalidOperationException($"Order {orderId} line {i} has malformed seller id: {sid}");
            }
            string addr = parts[2].ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(addr))
            {
                throw new InvalidOperationException($"Order {orderId} line {i} has empty seller address");
            }
            distinctSellers.Add((addr, ch));
            lineOwners.Add((i, addr, ch));
        }

        var createdAt = DateTimeOffset.UtcNow;

        using var tx = conn.BeginTransaction();
        try
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText =
                    "INSERT INTO orders (order_id, basket_id, order_json, status, created_at, buyer_address, buyer_chain_id, payment_reference) VALUES (@id, @bid, @json, @status, @created, @buyer, @chain, @payref) ON CONFLICT (order_id) DO NOTHING";
                cmd.Parameters.AddWithValue("@id", orderId);
                cmd.Parameters.AddWithValue("@bid", basketId);
                cmd.Parameters.AddWithValue("@json", NpgsqlTypes.NpgsqlDbType.Jsonb,
                    JsonSerializer.Serialize(order, Profiles.Models.JsonSerializerOptions.JsonLd));
                cmd.Parameters.AddWithValue("@status", order.OrderStatus ?? "https://schema.org/OrderPaymentDue");
                cmd.Parameters.AddWithValue("@created", createdAt);
                cmd.Parameters.AddWithValue("@buyer", (object?)buyerAddr ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@chain", (object?)buyerChain ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@payref", (object?)order.PaymentReference ?? DBNull.Value);
                int n = cmd.ExecuteNonQuery();
                if (n == 0)
                {
                    tx.Rollback();
                    return false; // already exists
                }
            }

            // Insert order_sellers
            if (distinctSellers.Count > 0)
            {
                using var ins = conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = string.Join('\n', distinctSellers.Select((s, i) =>
                    $"INSERT INTO order_sellers(order_id, seller_address, seller_chain_id, created_at) VALUES (@oid, @s{i}, @c{i}, @t) ON CONFLICT DO NOTHING;"));
                ins.Parameters.AddWithValue("@oid", orderId);
                ins.Parameters.AddWithValue("@t", createdAt);
                int idx = 0;
                foreach (var (addr, ch) in distinctSellers)
                {
                    ins.Parameters.AddWithValue($"@s{idx}", addr);
                    ins.Parameters.AddWithValue($"@c{idx}", ch);
                    idx++;
                }
                ins.ExecuteNonQuery();
            }

            // Insert order_line_sellers
            if (lineOwners.Count > 0)
            {
                using var ins2 = conn.CreateCommand();
                ins2.Transaction = tx;
                ins2.CommandText = string.Join('\n', lineOwners.Select((s, i) =>
                    $"INSERT INTO order_line_sellers(order_id, line_index, seller_address, seller_chain_id, created_at) VALUES (@oid, @li{i}, @sa{i}, @sc{i}, @t) ON CONFLICT DO NOTHING;"));
                ins2.Parameters.AddWithValue("@oid", orderId);
                ins2.Parameters.AddWithValue("@t", createdAt);
                int idx2 = 0;
                foreach (var (index, addr, ch) in lineOwners)
                {
                    ins2.Parameters.AddWithValue($"@li{idx2}", index);
                    ins2.Parameters.AddWithValue($"@sa{idx2}", addr);
                    ins2.Parameters.AddWithValue($"@sc{idx2}", ch);
                    idx2++;
                }
                ins2.ExecuteNonQuery();
            }

            // Mark one-off items as sold
            var oneOffItems = new List<(long chainId, string seller, string sku)>();
            for (int i = 0; i < order.AcceptedOffer.Count; i++)
            {
                var offer = order.AcceptedOffer[i];
                if (offer.IsOneOff == true)
                {
                    // Parse seller info from offer.Seller.Id (format: eip155:chain:address)
                    var sellerId = offer.Seller?.Id;
                    if (string.IsNullOrWhiteSpace(sellerId))
                    {
                        throw new InvalidOperationException($"Order {orderId} line {i} has no seller id");
                    }
                    var parts = sellerId.Split(':', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 3 || !long.TryParse(parts[1], out var chainId))
                    {
                        throw new InvalidOperationException($"Order {orderId} line {i} has malformed seller id: {sellerId}");
                    }
                    string sellerAddr = parts[2].ToLowerInvariant();
                    
                    // Get SKU from ordered item
                    if (i >= order.OrderedItem.Count || order.OrderedItem[i].OrderedItem?.Sku == null)
                    {
                        throw new InvalidOperationException($"Order {orderId} line {i} has missing ordered item SKU");
                    }
                    string sku = order.OrderedItem[i].OrderedItem.Sku!.ToLowerInvariant();
                    
                    // Enforce quantity = 1 for one-off items
                    if (order.OrderedItem[i].OrderQuantity != 1)
                    {
                        throw new InvalidOperationException($"One-off items must have quantity = 1. Order {orderId} line {i} has quantity {order.OrderedItem[i].OrderQuantity}");
                    }
                    
                    oneOffItems.Add((chainId, sellerAddr, sku));
                }
            }

            // Insert one-off sales records
            if (oneOffItems.Count > 0)
            {
                using var oneOffCmd = conn.CreateCommand();
                oneOffCmd.Transaction = tx;
                oneOffCmd.CommandText = @"
INSERT INTO one_off_sales(chain_id, seller_address, sku, order_id, ordered_at)
VALUES (@chain_id, @seller_address, @sku, @order_id, @ordered_at)
ON CONFLICT DO NOTHING;";

                var pChain = oneOffCmd.Parameters.AddWithValue("@chain_id", 0L);
                var pSeller = oneOffCmd.Parameters.AddWithValue("@seller_address", string.Empty);
                var pSku = oneOffCmd.Parameters.AddWithValue("@sku", string.Empty);
                var pOrderId = oneOffCmd.Parameters.AddWithValue("@order_id", orderId);
                var pOrderedAt = oneOffCmd.Parameters.AddWithValue("@ordered_at", createdAt);

                foreach (var (chainId, sellerAddr, sku) in oneOffItems)
                {
                    pChain.Value = chainId;
                    pSeller.Value = sellerAddr;
                    pSku.Value = sku;
                    pOrderId.Value = orderId;
                    pOrderedAt.Value = createdAt;

                    int affected = oneOffCmd.ExecuteNonQuery();
                    if (affected != 1)
                    {
                        throw new OneOffAlreadySoldException(chainId, sellerAddr, sku);
                    }
                }
            }

            tx.Commit();
            return true;
        }
        catch (Exception ex)
        {
            try { tx.Rollback(); } catch { }
            _logger.LogError(ex, "Failed to create order and projections for {OrderId}", orderId);
            throw;
        }
    }

    public OrderSnapshot? Get(string orderId)
    {
        return GetInternal(orderId, includeOutbox: true);
    }

    public OrderSnapshot? GetInternal(string orderId, bool includeOutbox)
    {
        using var conn = new NpgsqlConnection(_connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        // Read snapshot JSON plus DB-created timestamp and the latest live status
        cmd.CommandText = "SELECT order_json, created_at, status FROM orders WHERE order_id=@id";
        cmd.Parameters.AddWithValue("@id", orderId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        string json = reader.GetString(0);
        var order = JsonSerializer.Deserialize<OrderSnapshot>(json, Profiles.Models.JsonSerializerOptions.JsonLd);
        if (order is null) return null;
        try
        {
            // Ensure orderDate reflects the DB creation timestamp with time information (ISO 8601, UTC)
            var createdAt = reader.GetFieldValue<DateTimeOffset>(1);
            order.OrderDate = createdAt.ToUniversalTime().ToString("O");
        }
        catch
        {
            // If anything goes wrong reading created_at, leave the value from JSON as-is
        }
        try
        {
            // Overlay the current DB status so clients see the latest lifecycle state
            if (!reader.IsDBNull(2))
            {
                var dbStatus = reader.GetString(2);
                if (!string.IsNullOrWhiteSpace(dbStatus))
                {
                    order.OrderStatus = dbStatus;
                }
            }
        }
        catch
        {
            // If anything goes wrong reading status, leave the value from JSON as-is
        }
        if (includeOutbox)
        {
            try
            {
                var outboxItems = GetOutboxItems(orderId).ToList();
                order.Outbox = outboxItems.Select(o => new OrderOutboxItemDto
                {
                    Id = o.Id,
                    CreatedAt = o.CreatedAt,
                    Source = o.Source,
                    Payload = o.Payload
                }).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load outbox for order {OrderId}", orderId);
            }
        }
        else
        {
            order.Outbox = new List<OrderOutboxItemDto>();
        }
        return order;
    }

    public IEnumerable<OrderSnapshot> GetByBuyer(string buyerAddress, long chainId, int page, int pageSize)
    {
        if (string.IsNullOrWhiteSpace(buyerAddress)) yield break;

        int size = Math.Clamp(pageSize, 1, MarketConstants.Defaults.PageSizeMax);
        int p = Math.Max(page, 1);
        int offset = (p - 1) * size;

        using var conn = new NpgsqlConnection(_connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        // Include order_id for outbox lookup, plus live status
        cmd.CommandText = @"SELECT order_id, order_json, created_at, status FROM orders
WHERE buyer_address = @addr AND buyer_chain_id = @chain
ORDER BY created_at DESC
LIMIT @limit OFFSET @offset";
        cmd.Parameters.AddWithValue("@addr", buyerAddress.ToLowerInvariant());
        cmd.Parameters.AddWithValue("@chain", chainId);
        cmd.Parameters.AddWithValue("@limit", size);
        cmd.Parameters.AddWithValue("@offset", offset);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string orderId = reader.GetString(0);
            string json = reader.GetString(1);
            var order = JsonSerializer.Deserialize<OrderSnapshot>(json, Profiles.Models.JsonSerializerOptions.JsonLd);
            if (order is null) continue;
            try
            {
                var createdAt = reader.GetFieldValue<DateTimeOffset>(2);
                order.OrderDate = createdAt.ToUniversalTime().ToString("O");
            }
            catch { }
            try
            {
                if (!reader.IsDBNull(3))
                {
                    var dbStatus = reader.GetString(3);
                    if (!string.IsNullOrWhiteSpace(dbStatus))
                    {
                        order.OrderStatus = dbStatus;
                    }
                }
            }
            catch { }

            // Attach outbox items
            try
            {
                var outboxItems = GetOutboxItems(orderId).ToList();
                order.Outbox = outboxItems.Select(o => new OrderOutboxItemDto
                {
                    Id = o.Id,
                    CreatedAt = o.CreatedAt,
                    Source = o.Source,
                    Payload = o.Payload
                }).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load outbox for order {OrderId}", orderId);
            }
            yield return order;
        }
    }

    // PROJECTION READS (seller)
    public IEnumerable<string> GetOrderIdsBySeller(string sellerAddress, long chainId, int page, int pageSize)
    {
        if (string.IsNullOrWhiteSpace(sellerAddress)) yield break;
        int size = Math.Clamp(pageSize, 1, MarketConstants.Defaults.PageSizeMax);
        int p = Math.Max(page, 1);
        int offset = (p - 1) * size;

        using var conn = new NpgsqlConnection(_connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT s.order_id
FROM order_sellers s
JOIN orders o ON o.order_id = s.order_id
WHERE s.seller_address=@addr AND s.seller_chain_id=@chain
ORDER BY o.created_at DESC
LIMIT @limit OFFSET @offset";
        cmd.Parameters.AddWithValue("@addr", sellerAddress.ToLowerInvariant());
        cmd.Parameters.AddWithValue("@chain", chainId);
        cmd.Parameters.AddWithValue("@limit", size);
        cmd.Parameters.AddWithValue("@offset", offset);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            yield return reader.GetString(0);
        }
    }

    public IReadOnlyList<int> GetOrderLineIndicesForSeller(string orderId, string sellerAddress, long chainId)
    {
        var list = new List<int>();
        if (string.IsNullOrWhiteSpace(orderId) || string.IsNullOrWhiteSpace(sellerAddress)) return list;
        using var conn = new NpgsqlConnection(_connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT line_index FROM order_line_sellers WHERE order_id=@id AND seller_address=@addr AND seller_chain_id=@chain ORDER BY line_index ASC";
        cmd.Parameters.AddWithValue("@id", orderId);
        cmd.Parameters.AddWithValue("@addr", sellerAddress.ToLowerInvariant());
        cmd.Parameters.AddWithValue("@chain", chainId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(reader.GetInt32(0));
        }
        return list;
    }

    public bool OrderContainsSeller(string orderId, string sellerAddress, long chainId)
    {
        if (string.IsNullOrWhiteSpace(orderId) || string.IsNullOrWhiteSpace(sellerAddress)) return false;
        using var conn = new NpgsqlConnection(_connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM order_sellers WHERE order_id=@id AND seller_address=@addr AND seller_chain_id=@chain LIMIT 1";
        cmd.Parameters.AddWithValue("@id", orderId);
        cmd.Parameters.AddWithValue("@addr", sellerAddress.ToLowerInvariant());
        cmd.Parameters.AddWithValue("@chain", chainId);
        using var r = cmd.ExecuteReader();
        return r.Read();
    }

    public (string? BuyerAddress, long? BuyerChainId)? GetOwnerByOrderId(string orderId)
    {
        if (string.IsNullOrWhiteSpace(orderId)) return null;
        using var conn = new NpgsqlConnection(_connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT buyer_address, buyer_chain_id FROM orders WHERE order_id=@id";
        cmd.Parameters.AddWithValue("@id", orderId);
        using var reader = cmd.ExecuteReader();
        bool hasRow = reader.Read();
        if (!hasRow) return null;
        string? buyer = reader.IsDBNull(0) ? null : reader.GetString(0);
        long? chain = reader.IsDBNull(1) ? null : reader.GetInt64(1);
        return (buyer, chain);
    }

    public bool TryMarkPaidByReference(
        string paymentReference,
        long paidChainId,
        string txHash,
        int logIndex,
        string gatewayAddress,
        System.Numerics.BigInteger? amountWei,
        DateTimeOffset paidAt)
    {
        if (string.IsNullOrWhiteSpace(paymentReference)) return false;
        using var conn = new NpgsqlConnection(_connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        // Use aggregated payments table for matching: compare payments.total_amount_wei to expected_wei.
        // Use first_tx_hash/first_log_index as canonical linkage for paid_* fields.
        cmd.CommandText = @"
WITH pay AS (
  SELECT p.payment_reference,
         p.total_amount_wei,
         p.gateway_address,
         p.first_tx_hash,
         p.first_log_index
  FROM payments p
  WHERE p.payment_reference = @ref AND p.chain_id=@chain
), ord AS (
  SELECT o.order_id,
         NULLIF(o.order_json->'totalPaymentDue'->>'price', '')::numeric AS price,
         (NULLIF(o.order_json->'totalPaymentDue'->>'price', '')::numeric * 1000000000000000000)::numeric AS expected_wei
  FROM orders o
  WHERE o.payment_reference = @ref AND o.paid_at IS NULL
)
UPDATE orders o SET
  status = @processing,
  paid_at = COALESCE(o.paid_at, @paidAt),
  paid_tx_hash = COALESCE(p.first_tx_hash, @tx),
  paid_log_index = COALESCE(p.first_log_index, @log),
  paid_chain_id = @chain,
  paid_gateway = COALESCE(p.gateway_address, @gw),
  paid_amount_wei = COALESCE(p.total_amount_wei, CASE WHEN @amt IS NULL THEN NULL ELSE CAST(@amt AS numeric(78,0)) END)
FROM ord oo
LEFT JOIN pay p ON p.payment_reference = @ref
WHERE o.order_id = oo.order_id
  AND (
        oo.expected_wei IS NULL
        OR (p.total_amount_wei IS NOT NULL AND p.total_amount_wei >= oo.expected_wei)
      );
";
        cmd.Parameters.AddWithValue("@processing", Circles.Market.Api.StatusUris.PaymentProcessing);
        cmd.Parameters.AddWithValue("@paidAt", paidAt);
        cmd.Parameters.AddWithValue("@tx", (object)txHash);
        cmd.Parameters.AddWithValue("@log", logIndex);
        cmd.Parameters.AddWithValue("@chain", paidChainId);
        cmd.Parameters.AddWithValue("@gw", (object)gatewayAddress);
        if (amountWei is null)
        {
            cmd.Parameters.AddWithValue("@amt", DBNull.Value);
        }
        else
        {
            // Npgsql can map decimal; BigInteger → string then let PG cast
            cmd.Parameters.AddWithValue("@amt", (object)amountWei.Value.ToString());
        }
        cmd.Parameters.AddWithValue("@ref", paymentReference);
        int n = cmd.ExecuteNonQuery();
        return n > 0;
    }

    public bool TryMarkConfirmedByReference(string paymentReference, DateTimeOffset confirmedAt)
    {
        if (string.IsNullOrWhiteSpace(paymentReference)) return false;
        using var conn = new NpgsqlConnection(_connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE orders SET confirmed_at = COALESCE(confirmed_at, @t)
WHERE payment_reference = @ref AND paid_at IS NOT NULL AND confirmed_at IS NULL;";
        cmd.Parameters.AddWithValue("@t", confirmedAt);
        cmd.Parameters.AddWithValue("@ref", paymentReference);
        int n = cmd.ExecuteNonQuery();
        return n > 0;
    }

    public bool TryMarkFinalizedByReference(string paymentReference, DateTimeOffset finalizedAt)
    {
        if (string.IsNullOrWhiteSpace(paymentReference)) return false;
        using var conn = new NpgsqlConnection(_connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE orders SET finalized_at = COALESCE(finalized_at, @t),
                  status = @complete
WHERE payment_reference = @ref AND paid_at IS NOT NULL AND finalized_at IS NULL;";
        cmd.Parameters.AddWithValue("@t", finalizedAt);
        cmd.Parameters.AddWithValue("@ref", paymentReference);
        cmd.Parameters.AddWithValue("@complete", Circles.Market.Api.StatusUris.PaymentComplete);
        int n = cmd.ExecuteNonQuery();
        return n > 0;
    }

    public IEnumerable<OrderStatusHistoryEntry> GetStatusHistory(string orderId)
    {
        if (string.IsNullOrWhiteSpace(orderId))
        {
            yield break;
        }

        using var conn = new NpgsqlConnection(_connString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT old_status, new_status, changed_at
FROM orders_status_history
WHERE order_id = @id
ORDER BY changed_at ASC;";
        cmd.Parameters.AddWithValue("@id", orderId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string? oldStatus = reader.IsDBNull(0) ? null : reader.GetString(0);
            string newStatus = reader.GetString(1);
            var changedAt = reader.GetFieldValue<DateTimeOffset>(2);

            yield return new OrderStatusHistoryEntry(oldStatus, newStatus, changedAt);
        }
    }

    public IEnumerable<(string OrderId, string? BuyerAddress, long? BuyerChainId)> GetByPaymentReference(string paymentReference)
    {
        if (string.IsNullOrWhiteSpace(paymentReference)) yield break;

        using var conn = new NpgsqlConnection(_connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT order_id, buyer_address, buyer_chain_id FROM orders WHERE payment_reference = @ref";
        cmd.Parameters.AddWithValue("@ref", paymentReference);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string orderId = reader.GetString(0);
            string? buyer = reader.IsDBNull(1) ? null : reader.GetString(1);
            long? chain = reader.IsDBNull(2) ? null : reader.GetInt64(2);
            yield return (orderId, buyer, chain);
        }
    }

    // Outbox support
    public void AddOutboxItem(string orderId, string? source, JsonElement payload)
    {
        if (string.IsNullOrWhiteSpace(orderId)) return;
        using var conn = new NpgsqlConnection(_connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO order_outbox(order_id, payload, source) VALUES (@id, @payload, @source)";
        cmd.Parameters.AddWithValue("@id", orderId);
        var payloadJson = JsonSerializer.Serialize(payload, Profiles.Models.JsonSerializerOptions.JsonLd);
        cmd.Parameters.AddWithValue("@payload", NpgsqlTypes.NpgsqlDbType.Jsonb, payloadJson);
        cmd.Parameters.AddWithValue("@source", (object?)source ?? DBNull.Value);
        try
        {
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to insert outbox item for order {OrderId}", orderId);
        }
    }

    public IEnumerable<OrderOutboxItem> GetOutboxItems(string orderId)
    {
        if (string.IsNullOrWhiteSpace(orderId)) yield break;
        using var conn = new NpgsqlConnection(_connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, order_id, source, created_at, payload FROM order_outbox WHERE order_id=@id ORDER BY created_at ASC";
        cmd.Parameters.AddWithValue("@id", orderId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            long id = reader.GetInt64(0);
            string oid = reader.GetString(1);
            string? source = reader.IsDBNull(2) ? null : reader.GetString(2);
            DateTimeOffset createdAt = reader.GetFieldValue<DateTimeOffset>(3);
            JsonElement payload;
            try
            {
                // Read payload as text and parse to JsonElement
                string text = reader.GetString(4);
                using var doc = JsonDocument.Parse(text);
                payload = doc.RootElement.Clone();
            }
            catch
            {
                // Malformed JSON payload: skip row
                continue;
            }

            yield return new OrderOutboxItem(id, oid, source, createdAt, payload);
        }
    }
}

// Storage model for outbox rows
public sealed record OrderOutboxItem(
    long Id,
    string OrderId,
    string? Source,
    DateTimeOffset CreatedAt,
    System.Text.Json.JsonElement Payload
);
