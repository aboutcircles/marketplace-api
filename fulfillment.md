# Fulfillment Logic

This document summarizes how fulfillment currently works across `Circles.Market.Api` and the fulfillment adapters.

## 1) End-to-end fulfillment sequence

```mermaid
sequenceDiagram
    autonumber
    participant Chain as Chain Events / RPC
    participant Poller as CirclesPaymentsPoller
    participant PayFlow as OrderPaymentFlow
    participant Hooks as SseOrderLifecycleHooks
    participant Routes as PostgresMarketRouteStore
    participant FulfillClient as HttpOrderFulfillmentClient
    participant Adapter as Upstream Adapter (/fulfill/{chainId}/{seller})
    participant Orders as OrderStore (outbox)

    Chain->>Poller: PaymentReceived / block progression
    Poller->>PayFlow: HandleObservedTransfer / HandleConfirmed / HandleFinalization
    PayFlow->>Hooks: OnConfirmedAsync or OnFinalizedAsync
    Hooks->>Hooks: RunFulfillmentAsync(paymentReference, trigger)

    loop per matched order line (offer[i], item[i])
        Hooks->>Routes: TryResolveUpstreamAsync(chain,seller,sku,Fulfillment)
        alt route missing
            Hooks-->>Hooks: Skip line (warn log)
        else route found
            Hooks->>Hooks: effectiveTrigger = offer.trigger ?? "finalized"
            alt trigger matches current lifecycle event
                Hooks->>FulfillClient: FulfillAsync(endpoint, orderId, paymentRef, snapshot, trigger)
                FulfillClient->>Adapter: POST fulfillment payload
                Adapter-->>FulfillClient: JSON-LD result
                FulfillClient-->>Hooks: payload
                Hooks->>Orders: AddOutboxItem(orderId, "fulfillment", payload)
            else trigger mismatch
                Hooks-->>Hooks: Skip for this lifecycle event
            end
        end
    end
```

---

## 2) Trigger and routing decision logic

```mermaid
flowchart TD
    A[Lifecycle hook event: confirmed/finalized] --> B[Load orders by payment reference]
    B --> C[Iterate over line pairs by index]
    C --> D{Valid seller id and SKU?}
    D -- no --> D1[Skip line]
    D -- yes --> E[Resolve endpoint from DB route store]
    E --> F{Endpoint resolved?}
    F -- no --> F1[Skip line + warning]
    F -- yes --> G[effectiveTrigger = offer.CirclesFulfillmentTrigger or finalized]
    G --> H{effectiveTrigger == current event?}
    H -- no --> H1[Skip line]
    H -- yes --> I[Call IOrderFulfillmentClient.FulfillAsync]
    I --> J[Write adapter response to order outbox]
```

**Important:** fulfillment endpoint in offer snapshots is intentionally not trusted; endpoint is resolved from DB at fulfillment time.

---

## 3) Outbound fulfillment client behavior

```mermaid
flowchart TD
    A[FulfillAsync called] --> B{Endpoint is absolute HTTP or HTTPS?}
    B -- no --> B1[Throw argument exception]
    B -- yes --> C[Apply outbound timeout default 1500ms]
    C --> D[Try parse fulfill path from URL]
    D --> E[Try outbound auth header lookup]
    E --> F{Header found?}
    F -- yes --> G[Use fulfillment_trusted HttpClient]
    F -- no --> H[Use fulfillment_public HttpClient]
    H --> H1{Target private/local?}
    H1 -- yes --> H2[Block request with BadGateway]
    H1 -- no --> I
    G --> I[POST JSON payload with hop header]
    I --> J{2xx response?}
    J -- no --> J1[Log + throw]
    J -- yes --> K[Read response with size limit]
    K --> L[Return JSON payload]
```

Payload sent to adapter includes:
- `orderId`
- `paymentReference`
- `buyer`
- `items[]` (`sku`, `quantity`)
- `trigger`

---

## 4) Adapter-level processing

```mermaid
flowchart LR
    A[Fulfillment POST endpoint] --> B[Parse and validate FulfillmentRequest]
    B --> C[Require X-Circles-Service-Key with fulfill scope]
    C --> D{Adapter type}

    D -->|Odoo| E[TryBegin in fulfillment_runs for idempotency lock]
    E --> F{Lock acquired?}
    F -- no + existing ok/started --> F1[Return already processed/in-progress result]
    F -- no other --> F2[Return error]
    F -- yes --> G[Resolve SKU mappings + Odoo connection]
    G --> H[Create and confirm sale.order]
    H --> I[Mark run ok/error + optional tracking info]

    D -->|CodeDispenser| J[Resolve code pool mapping]
    J --> K{Ambiguous or none?}
    K -- yes --> K1[Return ambiguous/notApplicable]
    K -- no --> L[AssignManyAsync with idempotent DB semantics]
    L --> M[Return ok/depleted/error with codes]
```

---

## 5) Practical findings and caveats

1. **Default trigger is `finalized`** unless explicitly set to `confirmed` on the offer.
2. **Line iteration is index-based** (`offer[i]` with `item[i]`), bounded by `min(counts)`.
3. **Missing route or trigger mismatch is a soft skip**, not a hard error.
4. **API hook currently performs best-effort execution**; failures are logged and do not stop other lines.
5. **Potential duplicate adapter calls** can happen when multiple lines resolve to same endpoint and trigger (adapter idempotency mitigates this, especially Odoo via `fulfillment_runs`).

---

## 6) Duplicate-call semantics (adapter contract)

For the same idempotency key `(chainId, seller, paymentReference)`:

- If a run is already `started`, adapters must **not** start a second concurrent execution.
- If a run is already `ok`, adapters must **not** execute again.
- In both cases, adapters should return a deterministic replay-style response (`Already in progress` / `Already processed`) instead of re-running fulfillment logic.

This is the behavior frontend and implementor integrations should rely on.

### Error reconciliation policy

Run status transitions are:

- `started` -> `ok` on successful completion
- `started` -> `error` on failed completion

Reconciliation rules:

- `ok`: replay only, never re-execute.
- `started`: replay only, never re-execute by default.
- `error`: retriable; a later call may re-acquire and execute again.

This gives strict duplicate suppression for in-flight/successful runs while still allowing recovery from failed runs.

### Optional stale-started takeover (operator override)

By default, stale `started` runs are **not** taken over.

Operators can opt in to stale-started takeover with:

- Odoo:
  - `ODOO_FULFILLMENT_ALLOW_STARTED_TAKEOVER` (default: `false`)
  - `ODOO_FULFILLMENT_STALE_MINUTES` (default: `10`)
- CodeDispenser:
  - `CODE_FULFILLMENT_ALLOW_STARTED_TAKEOVER` (default: `false`)
  - `CODE_FULFILLMENT_STALE_MINUTES` (default: `10`)

When enabled, a `started` run older than the configured stale window may be re-acquired.

> Operational warning: enabling stale takeover can permit re-execution after long-running or interrupted calls. Keep disabled unless you explicitly want this recovery mode.
