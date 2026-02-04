# Circles Market API – Operator Aggregated Catalog and Cart

This service exposes:
- Operator Aggregated Catalog: read verified `product/*` links across many avatars (sellers) under an operator namespace.
- Cart + Orders APIs: create/patch/validate baskets, checkout into immutable orders, buyer- and seller-scoped reads.
- Active Sellers: list all currently configured and active seller addresses.

## Operator Aggregated Catalog

GET /api/operator/{operator}/catalog

Query parameters:
- avatars: repeated seller addresses to include (required at least once)
- chainId: target chain (defaults to server’s configured chain if omitted)
- start, end: UNIX seconds window; links are filtered to this window
- cursor, offset, pageSize: pagination controls; cursor is preferred; offset is supported for simple paging

Example:
GET /api/operator/0xoperator/catalog?avatars=0xsellerA&avatars=0xsellerB&chainId=100&start=1730000000&end=1731000000&pageSize=50

Notes:
- Addresses are case-insensitive but treated as lowercase internally.
- Uses strict verification (EOA low-S, Safe ERC-1271 bytes with from=signer).

## Active Sellers

GET /api/sellers

Returns all distinct seller addresses that have at least one enabled route configuration. Each entry includes the chainId and seller address.

Example:
GET /api/sellers

Example response:

```json
{
  "sellers": [
    {
      "chainId": 100,
      "seller": "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
    }
  ]
}
```

## Cart API highlights

Base path: /api/cart/v1

- POST /baskets → create a basket
- GET /baskets/{basketId} → read
- PATCH /baskets/{basketId} → update (server enforces item limits; client-offer snapshots are ignored)
- POST /baskets/{basketId}/validate → rule-driven validation (OfferRequiredSlots)
- POST /baskets/{basketId}/checkout → persist immutable Order snapshot (creates orderId)
- POST /baskets/{basketId}/preview → non-persisted Order snapshot

### Orders (buyer scope; JWT required)
- GET /orders/by-buyer → list buyer’s orders (newest first)
- GET /orders/{orderId} → single order (buyer)
- GET /orders/{orderId}/status-history → lifecycle history (buyer)

### Orders (seller scope; JWT required)
- GET /orders/by-seller → list sales for authenticated seller (filtered view)
- GET /orders/{orderId}/as-seller → single order for authenticated seller (filtered view)

Seller responses return SellerOrderDto only, built through a single seller-safe pipeline (projection → indices → builder). Multi-seller orders are filtered by authoritative line indices; outbox is not loaded on seller reads.

### Pagination
- `cursor` (string): preferred for stable paging.
- `offset` (integer, default 0): supported for simple paging.
- `pageSize` (integer, default 20, max 100).
- `page` (integer, default 1): alias for `(offset / pageSize) + 1` in some list endpoints.

## Operator Aggregated Catalog

The **Operator Aggregated Catalog** endpoint provides a convenient way to retrieve offers from multiple marketplace sellers in a single request.

### Endpoint

```
GET /api/operator/{operator}/catalog
```

- **{operator}** – The operator address that owns the catalog.
- **avatars** – One or more marketplace seller addresses whose offers you want to include. The parameter can be repeated to query multiple sellers.

### What it does
- Aggregates offers from the specified sellers.
- Returns a coherent list of offers, each enriched with the seller’s avatar information.
- Useful for building UI components that display a combined marketplace view.

### Example Request

```bash
curl "http://localhost:5084/api/operator/0xOperatorAddress/catalog?avatars=0xSellerA&avatars=0xSellerB&pageSize=10"
```

### Example Response (simplified)

```json
{
  "offers": [
    {
      "seller": "0xSellerA",
      "offerId": "123",
      "title": "Cool Product",
      "price": "10.0 CRC",
      "avatar": "https://ipfs.io/ipfs/Qm..."
    }
  ],
  "nextCursor": "...",
  "totalCount": 45
}
```

### Use Cases
- **Marketplace UI:** Show a unified list of items from several sellers.
- **Analytics:** Pull offers from many profiles for market analysis.

---

For more details on other endpoints, see the main repository README or the source code in this folder.
