### Context

#### Why we’re removing the legacy “seller-hosted feeds/fulfillment URLs in product data” now
- **Trust boundary is wrong today**: inventory and fulfillment URLs embedded in product/offers are effectively *untrusted control-plane inputs* coming from “the wild”. Market API then dereferences them, which is an SSRF and policy-enforcement problem (even with outbound guards).
- **Operator-centric publishing is the current product reality**: right now products are expected to be published and curated under the **marketplace operator namespace**. The operator is the party accountable for ensuring inventory and fulfillment are configured and reachable.
- **Operational correctness**: configuration belongs in a single place (operator-controlled DB), so you can guarantee Market API only answers for explicitly configured `(chainId, seller, sku)` tuples and can reliably support incident response, rollbacks, and auditing.

#### How we can re-add decentralization later (proposer pattern)
- **Goal**: allow sellers to host/store product documents in their own profiles again, while letting the operator remain the party that can *trust* what it reads.
- **Mechanism**: operator becomes the **proposer**:
    - operator creates the product document (including synthetic Market API URLs)
    - operator **signs** it
    - seller stores it “under the marketplace namespace” for storage/distribution
    - Market API validates the operator signature when it finds product data “in the wild”, which restores trust without requiring centralized storage
- **Result**: re-decentralization becomes safe because the operator can cryptographically assert, “I created and approved these links/fields”.

---

### Concrete task list (deliverables + acceptance criteria)

#### Task 1 — Define and document the new source of truth: DB routing for configured products
**Deliverable**
- Create a clear definition of “configured product” and routing resolution:
    - `inventory` route optional
    - `availability` route optional
    - `fulfillment` route optional
    - optional `is_one_off` flag (to preserve current semantics)

**Acceptance criteria**
- There is a single API (interface) in Market API that can answer:
    - `IsConfigured(chainId, seller, sku)`
    - `TryResolveUpstream(chainId, seller, sku, serviceKind)`
- “Configured” semantics are identical to the legacy behavior for products that have equivalent routes present.

---

#### Task 2 — Catalog output: emit only synthetic Market API URLs, gated by DB configuration
**Change**
- Update `Circles.Market.Api/Catalog/OperatorCatalogEndpoint.cs`:
    - Replace `hasInventory`/`hasAvailability` checks that currently depend on `offer.CirclesInventoryFeed` / `offer.CirclesAvailabilityFeed`.
    - Instead, decide what to emit based on DB route existence for `(chainId, seller, sku)`.
    - **Do not filter aggregated products by DB configuration.** Catalog aggregation always includes all products found; DB config only controls whether synthetic feed URLs are emitted.
    - Preserve the legacy "presence semantics":
        - if `inventory` route exists: emit both synthetic `availability` and synthetic `inventory`
        - if only `availability` route exists: emit synthetic `availability` only
        - if `is_one_off`: emit synthetic `availability` only

**Acceptance criteria**
- No catalog response contains any upstream URL taken from product/offer fields.
- For configured products, catalog contains the same feed fields “present vs absent” as before, but pointing to Market API.

---

#### Task 3 — Inventory/availability endpoints: remove legacy dereferencing and resolve upstream via DB only
**Change**
- Update `Circles.Market.Api/Inventory/InventoryEndpoints.cs`:
    - Stop reading `offer.CirclesAvailabilityFeed` / `offer.CirclesInventoryFeed` for routing.
    - First step in each endpoint: check configuration for `(chainId, seller, sku)`.
        - if not configured: return `404`.
    - Preserve legacy behavior by implementing the same preference order via DB:
        - `/availability/...`: prefer `availability` route; else derive from `inventory` route
        - `/inventory/...`: require `inventory` route
    - Keep existing outbound hardening and validation (loop header, private address blocking, schema validation, etc.).

**Acceptance criteria**
- Market API never fetches an upstream feed URL sourced from product data.
- For configured products, response shapes and validation behavior match legacy behavior.

---

#### Task 4 — One-off semantics: make “one-off” explicit (if required for parity)
**Change**
- Replace legacy inference `isOneOff = no inventoryFeed AND no availabilityFeed` with a DB-backed fact.
- Update `Circles.Market.Api/Cart/BasketCanonicalizer.cs`:
    - Stop computing `isOneOff` from `offer.Circles*Feed` fields.
    - Use DB configuration (e.g., `is_one_off`) to enforce `quantity == 1`.

**Acceptance criteria**
- Quantity rules for one-off items remain unchanged for configured one-offs.
- Removing product-field-based inference does not change ordering behavior.

---

#### Task 5 — Fulfillment: stop propagating upstream endpoints; route fulfillment via DB (and optionally proxy via Market API)
**Change**
- Update `Circles.Market.Api/Cart/BasketCanonicalizer.cs`:
    - Stop copying `offer.CirclesFulfillmentEndpoint` into `OfferSnapshot`.
    - Keep only the minimal hints required (or none), and rely on `(chainId, seller, sku)` at trigger time.
- Update the fulfillment trigger flow (e.g., `SseOrderLifecycleHooks` if present in the repo):
    - Resolve fulfillment upstream endpoint from DB for each relevant line.
    - If not configured: do not call anything (fail fast or skip with explicit logging, depending on desired semantics).
- Optional (recommended to fully satisfy “all emitted URLs point back to Market API”):
    - Add `POST /fulfill/{chainId}/{seller}/{sku}` in Market API that proxies to the configured upstream fulfillment service.

**Acceptance criteria**
- No order/basket snapshot can cause Market API to call an arbitrary URL.
- Fulfillment calls are only performed for configured tuples.

---

#### Task 6 — Migration/backfill plan + removal of legacy logic
**Deliverable**
- delete all code paths that use `offer.CirclesInventoryFeed`, `offer.CirclesAvailabilityFeed`, `offer.CirclesFulfillmentEndpoint` for routing decisions.
- Migration will be handled separate and manually.

**Acceptance criteria**
- A configured product behaves the same as pre-change in:
    - catalog feed presence
    - availability behavior (including derived availability)
    - inventory behavior
    - one-off ordering rules
    - fulfillment triggering
- An unconfigured product consistently returns `404` for synthetic endpoints. Catalog aggregation may still include the product, but it must not emit synthetic inventory/availability feed fields unless DB routing says they exist.

---

### “Add it later” tasks (proposer pattern backlog, informational)

#### Task L1 — Define a signed product envelope
- Define a canonical payload (product document) and a signature scheme:
    - operator key material / key rotation plan
    - signature verification rules
    - what exactly is signed (at minimum: `(chainId, operator, seller, sku)` + all emitted link fields)

#### Task L2 — Operator proposer workflow
- Implement an operator-side flow that:
    - creates the product document
    - embeds only synthetic Market API URLs
    - signs the document
    - produces an artifact the seller can store

#### Task L3 — Seller storage under marketplace namespace
- Define the storage convention (“seller stores it in their profile under operator namespace”).
- Ensure Market API catalog aggregation reads these documents and:
    - verifies the operator signature
    - ignores unsigned/unverifiable documents

#### Task L4 — Gradual re-decentralization
- Add a feature flag / config toggle:
    - `OperatorNamespaceOnly` (current state)
    - `SignedSellerStorageEnabled` (future state)
- Maintain DB routing as the runtime enforcement point (still required for gating and safety), even if product storage decentralizes.

---

### Notes on keeping behavior identical for configured products
- “Same behavior” should be validated by a test matrix comparing:
    - old selection logic (availability preferred over inventory)
    - schema validation and error mapping
    - one-off quantity restriction
    - status codes for missing routes (`404`) and upstream failures (`502`, `508`)
- DB configuration must encode enough information to reproduce the legacy decision tree without reading product URL fields.
