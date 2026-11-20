# 0004 Checkout consistency: the order is the price lock

## Status

Accepted, 2026-11-20

## Context

Partner prices and stock change at any time, and Lumen's copy can be up to 15 minutes old ([0003](0003-partner-integration-adapters-sync-image-proxy.md)).
A customer must never pay a price they have not seen, never pay for something that cannot be supplied, and never be charged twice; Lumen's own stock must never be oversold.
Customers double-click, open several tabs and refresh in the middle of a payment. Partners offer no way to reserve stock.

## Considered options

- A separate quote (checkout session) priced at review, with its own 15-minute TTL, turned into an order at payment.
- Reserve stock when an item is added to the cart.
- Distributed locks (Redis or PostgreSQL advisory locks) around checkout, per product or per customer.
- The order itself as the price lock, guarded by a unique key, conditional updates and check constraints (chosen).

## Decision

1. Place order sends the total the customer reviewed and a key the client generates once per checkout. The server checks each partner line live (Harbor item, Kestrel `/stock`, 5 s timeout; BR-CHK-02), then re-prices every line with the same `PricingService` the cart and review will use. Any difference, up or down, returns 409 with each changed line (old and new) and the new total, and creates nothing (BR-CHK-01).
2. Otherwise one transaction creates the order (AwaitingPayment, with each line's price, tax and FX rate and each shipment's fee) and reserves own stock line by line: `UPDATE Products SET Reserved = Reserved + @q WHERE Id = @id AND OnHand - Reserved >= @q`. A line that updates no row rolls everything back and returns 409. Check constraints keep `0 <= Reserved <= OnHand` (BR-STOCK-01).
3. The checkout key is unique on the order, so a repeated Place order returns the existing order.
4. The order holds its prices and reserved stock for 15 minutes. Payment charges the order total, never an amount from the client, and starts only by claiming the order: `UPDATE Orders SET Status = 'PaymentProcessing' WHERE Id = @id AND Status = 'AwaitingPayment'`. So one payment runs at a time and a paid order cannot be paid again (BR-PAY-01). The order expires, releasing its stock, when the hold ends with no payment in progress or after the fifth failed attempt.
5. No partner or payment I/O inside a database transaction: partner checks run before the transaction opens, and the gateway is called after the claim has committed.

Why: each guarantee is one database rule (a unique index, a conditional update, a check constraint) that PostgreSQL enforces under any concurrency, with no extra entity to expire and no lock service to run.

## Consequences

- Good: correctness depends neither on timing nor on locks held across I/O, and integration tests on real PostgreSQL can prove it (the last-unit race and double-submit scenarios of BR-STOCK-01 and BR-PAY-01).
- Good: no quote table to expire and clean up; what was priced is exactly what is charged and shown on the confirmation.
- Bad: every placed but unpaid checkout leaves an Expired order row.
- Bad: partner stock is checked, not held. It can still run out between the check and the partner order; that is handled after payment with a refund ([0005](0005-payments-charge-then-refund-one-partner-order-per-line.md)).
- Bad: a price drop also needs a re-confirm click (the strictest reading of "never pay a price not seen").
- At scale: one live Harbor check per line spends the shared Harbor budget, so at sale peaks it must fall back to fresh sync data when tokens run short. A best-selling own product makes its row a hot spot: short transactions now, split stock rows or a reservation table later.

## More information

- Rules: BR-CHK-01, BR-CHK-02, BR-STOCK-01, BR-PAY-01 in [BUSINESS-RULES.md](../BUSINESS-RULES.md#checkout-and-stock)
- Stock check constraints: [ProductConfiguration.cs](../../src/Lumen.Api/Catalog/ProductConfiguration.cs)
- Pricing: [PricingService.cs](../../src/Lumen.Domain/Pricing/PricingService.cs); hold and attempts: [PaymentOptions.cs](../../src/Lumen.Domain/Payments/PaymentOptions.cs), [PaymentWindowPolicy.cs](../../src/Lumen.Domain/Payments/PaymentWindowPolicy.cs); order states: [OrderTransitions.cs](../../src/Lumen.Domain/Orders/OrderTransitions.cs), [Orders/README.md](../../src/Lumen.Domain/Orders/README.md)
- PostgreSQL, Read Committed isolation (a concurrent `UPDATE ... WHERE` re-checks the row it waited for): <https://www.postgresql.org/docs/18/transaction-iso.html>
