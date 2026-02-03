# Odoo – API README

> **Note:** These env vars are for running the curl examples against Odoo directly.
> The Odoo adapter itself is configured via its database tables (`odoo_connections`, `inventory_mappings`) using the Odoo admin API on the **adapter admin port** (default `5688`).
> Admin JWTs are minted by the Market API admin app and validated by the Odoo adapter.

> **Base host:** `https://your.odoo.com`
> **Endpoints used:**
>
> * JSON-RPC: `POST /jsonrpc` (Odoo JSON-RPC 2.0)

---

## Quickstart

### Set connection vars (recommended)

```bash
export ODOO_URL='https://your.odoo.com'
export ODOO_DB='your-odoo-db'
export ODOO_UID='your-odoo-uid'
export ODOO_KEY='your-odoo-key'
```

### JSON-RPC basics

Most calls go to:

* `POST ${ODOO_URL}/jsonrpc`

Request shape:

* `jsonrpc`: `"2.0"`
* `method`: `"call"`
* `params.service`: `"object"`
* `params.method`: `"execute_kw"`
* `params.args`: `[db, uid, api_key_or_password, model, method, ...methodArgs]`

> Note: The Postman collection shows some JSON-RPC calls as `GET` with a body. In practice, JSON-RPC is typically sent via **POST**.

---

## 1) Get stock by product code (product.template.search_read)

**What it does:** Queries Odoo for a product template by `default_code` (e.g. `FOOD`) and returns basic stock info.

**Endpoint:** `POST https://your.odoo.com/jsonrpc`

```bash
curl -X POST "${ODOO_URL}/jsonrpc" \
  -H 'Content-Type: application/json' \
  --data-raw '{
    "jsonrpc": "2.0",
    "method": "call",
    "id": 1,
    "params": {
      "service": "object",
      "method": "execute_kw",
      "args": [
        "'"${ODOO_DB}"'",
        '"${ODOO_UID}"',
        "'"${ODOO_KEY}"'",
        "product.template",
        "search_read",
        [[[ "default_code", "=", "FOOD" ]]],
        { "fields": ["id", "display_name", "qty_available"], "limit": 1 }
      ]
    }
  }'
```

**Returns (typical):**

* `id` (product template id)
* `display_name`
* `qty_available`

---

## 3) Get operation details by origin (stock.picking.search_read)

**What it does:** Looks up a stock picking (delivery/operation) by its `origin` (example: `S0001`) and returns carrier + tracking info.

```bash
curl -X POST "${ODOO_URL}/jsonrpc" \
  -H 'Content-Type: application/json' \
  --data-raw '{
    "jsonrpc": "2.0",
    "method": "call",
    "id": 1,
    "params": {
      "service": "object",
      "method": "execute_kw",
      "args": [
        "'"${ODOO_DB}"'",
        '"${ODOO_UID}"',
        "'"${ODOO_KEY}"'",
        "stock.picking",
        "search_read",
        [[[ "origin", "=", "S0001" ]]],
        { "fields": ["id", "carrier_id", "carrier_tracking_ref"], "limit": 1 }
      ]
    }
  }'
```

**Returns (typical):**

* `id` (picking id)
* `carrier_id`
* `carrier_tracking_ref`

---

## 4) Get customer ID by name (res.partner.search_read)

**What it does:** Searches partners by name (case-insensitive “contains” via `ilike`) and returns the first match.

```bash
curl -X POST "${ODOO_URL}/jsonrpc" \
  -H 'Content-Type: application/json' \
  --data-raw '{
    "jsonrpc": "2.0",
    "method": "call",
    "id": 1,
    "params": {
      "service": "object",
      "method": "execute_kw",
      "args": [
        "'"${ODOO_DB}"'",
        '"${ODOO_UID}"',
        "'"${ODOO_KEY}"'",
        "res.partner",
        "search_read",
        [[[ "name", "ilike", "%Simplified Invoice Partner (ES)%" ]]],
        { "fields": ["id", "name"], "limit": 1 }
      ]
    }
  }'
```

**Returns (typical):**

* `id` (partner id)
* `name`

---

## 5) Get product ID by code (product.template.search_read)

**What it does:** Searches product templates by exact `default_code` and returns id + display data.

```bash
curl -X POST "${ODOO_URL}/jsonrpc" \
  -H 'Content-Type: application/json' \
  --data-raw '{
    "jsonrpc": "2.0",
    "method": "call",
    "id": 1,
    "params": {
      "service": "object",
      "method": "execute_kw",
      "args": [
        "'"${ODOO_DB}"'",
        '"${ODOO_UID}"',
        "'"${ODOO_KEY}"'",
        "product.template",
        "search_read",
        [[[ "default_code", "=", "FOOD" ]]],
        { "fields": ["id", "display_name", "default_code"], "limit": 1 }
      ]
    }
  }'
```

**Returns (typical):**

* `id` (product template id)
* `display_name`
* `default_code`

---

## 6) Create sale order (sale.order.create)

**What it does:** Creates a `sale.order` for a given partner and adds one order line (Odoo “x2many command” format).

```bash
curl -X POST "${ODOO_URL}/jsonrpc" \
  -H 'Content-Type: application/json' \
  --data-raw '{
    "jsonrpc": "2.0",
    "method": "call",
    "id": 1,
    "params": {
      "service": "object",
      "method": "execute_kw",
      "args": [
        "'"${ODOO_DB}"'",
        '"${ODOO_UID}"',
        "'"${ODOO_KEY}"'",
        "sale.order",
        "create",
        [[{
          "partner_id": 13,
          "partner_invoice_id": 13,
          "partner_shipping_id": 13,
          "order_line": [
            [0, 0, {
              "product_id": 25,
              "product_uom_qty": 1
            }]
          ]
        }]]
      ]
    }
  }' | jq
```

**Notes**

* `partner_id`, `partner_invoice_id`, `partner_shipping_id` are numeric **partner IDs** (from `res.partner`).
* `order_line` uses Odoo’s command list format. `[0, 0, {...}]` means “create a new related record with these values”.
* `product_id` here is numeric. Depending on your Odoo setup, you may need a `product.product` id (variant) rather than a `product.template` id.

---

# Listing products

In Odoo there are two common product models:

* `product.template`: the “template” (catalog item)
* `product.product`: the “variant” (what you actually sell/stock, often what `sale.order.line.product_id` expects)

If you’re not sure which one you need for orders, start with `product.product`.

---

## List product templates (paged)

```bash
curl -X POST "${ODOO_URL}/jsonrpc" \
  -H 'Content-Type: application/json' \
  --data-raw '{
    "jsonrpc": "2.0",
    "method": "call",
    "id": 10,
    "params": {
      "service": "object",
      "method": "execute_kw",
      "args": [
        "'"${ODOO_DB}"'",
        '"${ODOO_UID}"',
        "'"${ODOO_KEY}"'",
        "product.template",
        "search_read",
        [[]],
        {
          "fields": ["id", "display_name", "default_code", "list_price", "qty_available", "active"],
          "limit": 100,
          "offset": 0,
          "order": "id asc"
        }
      ]
    }
  }' | jq
```

### Next page

Set `offset` to `100`, `200`, etc.

---

## Check shipping status
Order-id: 87
```shell
curl -X POST "${ODOO_URL}/jsonrpc" \
  -H 'Content-Type: application/json' \
  --data-raw '{
    "jsonrpc": "2.0",
    "method": "call",
    "id": 12,
    "params": {
      "service": "object",
      "method": "execute_kw",
      "args": [
        "'"${ODOO_DB}"'",
        '"${ODOO_UID}"',
        "'"${ODOO_KEY}"'",
        "stock.picking",
        "search_read",
        [[
          ["origin", "=", "87"]
        ]],
        {
          "fields": ["id", "carrier_id", "carrier_tracking_ref"],
          "limit": 1
        }
      ]
    }
  }' | jq

```


## List product variants (paged)

```bash
curl -X POST "${ODOO_URL}/jsonrpc" \
  -H 'Content-Type: application/json' \
  --data-raw '{
    "jsonrpc": "2.0",
    "method": "call",
    "id": 11,
    "params": {
      "service": "object",
      "method": "execute_kw",
      "args": [
        "'"${ODOO_DB}"'",
        '"${ODOO_UID}"',
        "'"${ODOO_KEY}"'",
        "product.product",
        "search_read",
        [[]],
        {
          "fields": ["id", "display_name", "default_code", "product_tmpl_id", "barcode", "qty_available", "active"],
          "limit": 100,
          "offset": 0,
          "order": "id asc"
        }
      ]
    }
  }' | jq
```

---

## Common product filters

### Only active products

Use domain: `[[ "active", "=", true ]]`

### Only products with a code

Use domain: `[[ "default_code", "!=", false ]]`

Example (active + has code):

```bash
curl -X POST "${ODOO_URL}/jsonrpc" \
  -H 'Content-Type: application/json' \
  --data-raw '{
    "jsonrpc": "2.0",
    "method": "call",
    "id": 12,
    "params": {
      "service": "object",
      "method": "execute_kw",
      "args": [
        "'"${ODOO_DB}"'",
        '"${ODOO_UID}"',
        "'"${ODOO_KEY}"'",
        "product.product",
        "search_read",
        [[[ "active", "=", true ], [ "default_code", "!=", false ]]],
        { "fields": ["id", "display_name", "default_code"], "limit": 100, "offset": 0, "order": "id asc" }
      ]
    }
  }'
```

---

# Handy Odoo JSON-RPC defaults for an order pipeline

This section is the “stuff you usually end up needing” when wiring up sales → warehouse → invoice.

## A) Find or create a customer

### Search customer by exact name

```bash
curl -X POST "${ODOO_URL}/jsonrpc" \
  -H 'Content-Type: application/json' \
  --data-raw '{
    "jsonrpc": "2.0",
    "method": "call",
    "id": 20,
    "params": {
      "service": "object",
      "method": "execute_kw",
      "args": [
        "'"${ODOO_DB}"'",
        '"${ODOO_UID}"',
        "'"${ODOO_KEY}"'",
        "res.partner",
        "search_read",
        [[[ "name", "=", "ACME GmbH" ]]],
        { "fields": ["id", "name", "email", "vat", "street", "city", "zip", "country_id"], "limit": 10 }
      ]
    }
  }' | jq
```

### Create a customer (basic)

```bash
curl -X POST "${ODOO_URL}/jsonrpc" \
  -H 'Content-Type: application/json' \
  --data-raw '{
    "jsonrpc": "2.0",
    "method": "call",
    "id": 21,
    "params": {
      "service": "object",
      "method": "execute_kw",
      "args": [
        "'"${ODOO_DB}"'",
        '"${ODOO_UID}"',
        "'"${ODOO_KEY}"'",
        "res.partner",
        "create",
        [{
          "name": "Customer 1",
          "email": "test@example.com",
          "street": "Test street 123",
          "zip": "12345",
          "city": "Testcity",
          "country_id": 00
        }]
      ]
    }
  }' | jq
```

**Notes**

* `country_id` is a many2one id; you can resolve it via `res.country.search_read` if you don’t already have it.

---

## B) Resolve product IDs reliably

If your `sale.order.line.product_id` expects `product.product` ids, resolve by `default_code` from `product.product` first.

### Get `product.product` by code

```bash
curl -X POST "${ODOO_URL}/jsonrpc" \
  -H 'Content-Type: application/json' \
  --data-raw '{
    "jsonrpc": "2.0",
    "method": "call",
    "id": 30,
    "params": {
      "service": "object",
      "method": "execute_kw",
      "args": [
        "'"${ODOO_DB}"'",
        '"${ODOO_UID}"',
        "'"${ODOO_KEY}"'",
        "product.product",
        "search_read",
        [[[ "default_code", "=", "FOOD" ]]],
        { "fields": ["id", "display_name", "default_code", "product_tmpl_id"], "limit": 1 }
      ]
    }
  }'
```

---

## C) Create a sale order with common defaults

A few fields that often matter in real pipelines:

* `client_order_ref`: external order reference (useful for idempotency)
* `pricelist_id`: price rules
* `payment_term_id`: invoice payment terms
* `fiscal_position_id`: tax mapping
* `warehouse_id`: where fulfillment happens
* `carrier_id`: delivery carrier selection
* `note`: free-form note printed on documents

Example:

```bash
curl -X POST "${ODOO_URL}/jsonrpc" \
  -H 'Content-Type: application/json' \
  --data-raw '{
    "jsonrpc": "2.0",
    "method": "call",
    "id": 40,
    "params": {
      "service": "object",
      "method": "execute_kw",
      "args": [
        "'"${ODOO_DB}"'",
        '"${ODOO_UID}"',
        "'"${ODOO_KEY}"'",
        "sale.order",
        "create",
        [[{
          "partner_id": 9,
          "partner_invoice_id": 9,
          "partner_shipping_id": 9,
          "client_order_ref": "EXT-ORDER-12345",
          "pricelist_id": 1,
          "payment_term_id": 1,
          "warehouse_id": 1,
          "carrier_id": 1,
          "note": "Leave at reception",
          "order_line": [
            [0, 0, { "product_id": 3, "product_uom_qty": 3, "price_unit": 50 }],
            [0, 0, { "product_id": 7, "product_uom_qty": 1, "price_unit": 100 }]
          ]
        }]]
      ]
    }
  }'
```

**Idempotency tip**

* Before creating, search by `client_order_ref` and/or `origin`/`name` depending on what you control. That prevents duplicates if you retry.

### Search an existing sale order by external ref

```bash
curl -X POST "${ODOO_URL}/jsonrpc" \
  -H 'Content-Type: application/json' \
  --data-raw '{
    "jsonrpc": "2.0",
    "method": "call",
    "id": 41,
    "params": {
      "service": "object",
      "method": "execute_kw",
      "args": [
        "'"${ODOO_DB}"'",
        '"${ODOO_UID}"',
        "'"${ODOO_KEY}"'",
        "sale.order",
        "search_read",
        [[[ "client_order_ref", "=", "EXT-ORDER-12345" ]]],
        { "fields": ["id", "name", "state", "amount_total", "partner_id"], "limit": 10 }
      ]
    }
  }'
```

---

## D) Confirm the sale order (turn quotation into sales order)

Usually the next step is `action_confirm`.

```bash
curl -X POST "${ODOO_URL}/jsonrpc" \
  -H 'Content-Type: application/json' \
  --data-raw '{
    "jsonrpc": "2.0",
    "method": "call",
    "id": 50,
    "params": {
      "service": "object",
      "method": "execute_kw",
      "args": [
        "'"${ODOO_DB}"'",
        '"${ODOO_UID}"',
        "'"${ODOO_KEY}"'",
        "sale.order",
        "action_confirm",
        [[123]]
      ]
    }
  }'
```

* Replace `123` with the `sale.order` id.

---

## E) Get delivery pickings created for an order

Once confirmed, Odoo typically creates one or more `stock.picking` records.

### Find pickings by `origin` (often equals sale order name like `S0001`)

```bash
curl -X POST "${ODOO_URL}/jsonrpc" \
  -H 'Content-Type: application/json' \
  --data-raw '{
    "jsonrpc": "2.0",
    "method": "call",
    "id": 60,
    "params": {
      "service": "object",
      "method": "execute_kw",
      "args": [
        "'"${ODOO_DB}"'",
        '"${ODOO_UID}"',
        "'"${ODOO_KEY}"'",
        "stock.picking",
        "search_read",
        [[[ "origin", "=", "S0001" ]]],
        { "fields": ["id", "name", "state", "picking_type_id", "carrier_id", "carrier_tracking_ref"], "limit": 50 }
      ]
    }
  }'
```

---

## F) Reserve stock (assign) for a picking

This is commonly `action_assign` on `stock.picking`.

```bash
curl -X POST "${ODOO_URL}/jsonrpc" \
  -H 'Content-Type: application/json' \
  --data-raw '{
    "jsonrpc": "2.0",
    "method": "call",
    "id": 61,
    "params": {
      "service": "object",
      "method": "execute_kw",
      "args": [
        "'"${ODOO_DB}"'",
        '"${ODOO_UID}"',
        "'"${ODOO_KEY}"'",
        "stock.picking",
        "action_assign",
        [[456]]
      ]
    }
  }'
```

---

## G) Validate a picking (ship / mark done)

Often `button_validate` on `stock.picking`.

```bash
curl -X POST "${ODOO_URL}/jsonrpc" \
  -H 'Content-Type: application/json' \
  --data-raw '{
    "jsonrpc": "2.0",
    "method": "call",
    "id": 62,
    "params": {
      "service": "object",
      "method": "execute_kw",
      "args": [
        "'"${ODOO_DB}"'",
        '"${ODOO_UID}"',
        "'"${ODOO_KEY}"'",
        "stock.picking",
        "button_validate",
        [[456]]
      ]
    }
  }'
```

**Note**

* Some configurations require setting `qty_done` on move lines before validation. If `button_validate` complains, you’ll likely need to update `stock.move.line` records for that picking.

---

## H) Create and post an invoice

Odoo versions differ on exact method names. A common pattern:

* Call an invoice creation action from `sale.order` (often `action_create_invoice` / `_create_invoices`)
* Then post the invoice via `account.move.action_post`

### Find invoices related to an order (by invoice_origin)

```bash
curl -X POST "${ODOO_URL}/jsonrpc" \
  -H 'Content-Type: application/json' \
  --data-raw '{
    "jsonrpc": "2.0",
    "method": "call",
    "id": 70,
    "params": {
      "service": "object",
      "method": "execute_kw",
      "args": [
        "'"${ODOO_DB}"'",
        '"${ODOO_UID}"',
        "'"${ODOO_KEY}"'",
        "account.move",
        "search_read",
        [[[ "invoice_origin", "=", "S0001" ]]],
        { "fields": ["id", "name", "state", "amount_total", "payment_state"], "limit": 50 }
      ]
    }
  }'
```

### Post an invoice (account.move.action_post)

```bash
curl -X POST "${ODOO_URL}/jsonrpc" \
  -H 'Content-Type: application/json' \
  --data-raw '{
    "jsonrpc": "2.0",
    "method": "call",
    "id": 71,
    "params": {
      "service": "object",
      "method": "execute_kw",
      "args": [
        "'"${ODOO_DB}"'",
        '"${ODOO_UID}"',
        "'"${ODOO_KEY}"'",
        "account.move",
        "action_post",
        [[789]]
      ]
    }
  }'
```

---

## I) Check stock with more detail (stock.quant)

`qty_available` on products is convenient, but for warehouse/location-specific stock, `stock.quant` is the go-to.

```bash
curl -X POST "${ODOO_URL}/jsonrpc" \
  -H 'Content-Type: application/json' \
  --data-raw '{
    "jsonrpc": "2.0",
    "method": "call",
    "id": 80,
    "params": {
      "service": "object",
      "method": "execute_kw",
      "args": [
        "'"${ODOO_DB}"'",
        '"${ODOO_UID}"',
        "'"${ODOO_KEY}"'",
        "stock.quant",
        "search_read",
        [[[ "product_id", "=", 3 ]]],
        { "fields": ["id", "product_id", "location_id", "quantity", "reserved_quantity"], "limit": 200 }
      ]
    }
  }'
```

---

# General-purpose helpers

## 1) Get field metadata for a model (fields_get)

Useful when you’re not sure which fields exist / what types they have.

```bash
curl -X POST "${ODOO_URL}/jsonrpc" \
  -H 'Content-Type: application/json' \
  --data-raw '{
    "jsonrpc": "2.0",
    "method": "call",
    "id": 90,
    "params": {
      "service": "object",
      "method": "execute_kw",
      "args": [
        "'"${ODOO_DB}"'",
        '"${ODOO_UID}"',
        "'"${ODOO_KEY}"'",
        "sale.order",
        "fields_get",
        [],
        { "attributes": ["string", "type", "required", "readonly", "relation"] }
      ]
    }
  }'
```

## 2) Count records without fetching them (search_count)

```bash
curl -X POST "${ODOO_URL}/jsonrpc" \
  -H 'Content-Type: application/json' \
  --data-raw '{
    "jsonrpc": "2.0",
    "method": "call",
    "id": 91,
    "params": {
      "service": "object",
      "method": "execute_kw",
      "args": [
        "'"${ODOO_DB}"'",
        '"${ODOO_UID}"',
        "'"${ODOO_KEY}"'",
        "product.product",
        "search_count",
        [[[ "active", "=", true ]]]
      ]
    }
  }'
```

## 3) Read specific record IDs (read)

```bash
curl -X POST "${ODOO_URL}/jsonrpc" \
  -H 'Content-Type: application/json' \
  --data-raw '{
    "jsonrpc": "2.0",
    "method": "call",
    "id": 92,
    "params": {
      "service": "object",
      "method": "execute_kw",
      "args": [
        "'"${ODOO_DB}"'",
        '"${ODOO_UID}"',
        "'"${ODOO_KEY}"'",
        "sale.order",
        "read",
        [[123]],
        { "fields": ["id", "name", "state", "client_order_ref", "amount_total"] }
      ]
    }
  }'
```

---

# Security notes

* Treat `ODOO_KEY` like a password. Don’t commit it into a repo or paste it into logs.
