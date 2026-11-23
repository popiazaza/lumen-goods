# 0005 Payments: in-process mock gateway, charge then refund, one partner order per line

## Status

Accepted, 2026-11-20

## Context

Customers pay once for the whole order, by card (a mock with an exact result per test card) or from a USD wallet.
Partners can still refuse after payment: Harbor answers 400, 409 or 422 at once; Kestrel decides 10–30 s later and rejects a whole purchase order if any line is short. Neither partner can cancel an order.
The spec talks about "amounts charged" and "any refund". Card 0042 answers after 20 s; card 0059 errors first and approves when the same payment is retried.

## Considered options

- Authorize the full total, wait for every partner decision, capture only the accepted lines (what Stripe and Adyen offer for partial fulfilment).
- Charge the full total at payment, refund the lines partners reject (chosen).
- One partner order per seller, resubmitting the remaining lines after a whole-order rejection.
- One partner order per line, sent by a job that reads database rows (chosen), or through a message broker.

## Decision

- **Mock gateway** in process behind `IPaymentGateway`. The test cards are one table (`CardBehaviours`), special numbers matched before the Luhn rule. Only brand and last 4 digits are stored.
- **Charge, then refund.** A rejected line refunds its subtotal and its own tax, plus that seller's shipping once all of its lines are rejected, never more than the amount charged (inconsistent data throws), to the original method: card refund or wallet credit (`RefundCalculator`, BR-ORD-03). This follows the spec's wording and keeps one money path, with no authorization expiry to manage.
- **Never twice.** A payment row's id is the gateway idempotency key. A gateway error, not a decline, is retried once automatically with the same id, so 0059 is approved with one charge (BR-PAY-05). Try again on a Failed payment the gateway cannot settle yet reuses its row and id, with the same card only; every other attempt is a new row. Declines are never retried. At most 5 attempts per order.
- **Slow gateway.** The charge runs in a background task, not inside the HTTP request: the request returns "processing", the page polls the payment status, and refreshing or closing the page cancels nothing (BR-PAY-04).
- **Wallet:** a balance with `CHECK (Balance >= 0)`, debited by one conditional `UPDATE` plus a log row in the same transaction. One payment method per attempt (wallet or card, never split); after a decline the customer may switch (BR-PAY-06, A-38).
- **Partner orders only after payment.** The transaction that marks the order Paid writes one `PartnerOrders` row per partner line, with the reference `{orderNo}-{line}` reused on every retry, so Harbor's 200 replay and Kestrel's 409 `DUPLICATE_REFERENCE` count as success. A hosted job polls those rows, submits them and polls Kestrel tickets (BR-ORD-01). One order per line means Kestrel's whole-order rejection hits exactly one line. The rows are the queue; there is no broker. A line is refunded automatically only when its partner rejected it or never received it; when a send's outcome is unknown (a timeout after sending, a 5xx) the partner may hold the order, so after 30 minutes the line goes to an admin instead (BR-ORD-04).

## Consequences

- Good: few moving parts, and the logic is unit-tested before checkout exists (state tables, refunds, card table, one-time codes).
- Good: the partner-order rows commit with the payment result, so they act as an outbox: a crash after the commit resumes, a crash before it sends nothing.
- Bad: a card is charged for lines that may be refunded minutes later. With a real PSP a refund costs fees and takes days to reach the customer; we would not run production this way.
- Bad: one order per line multiplies partner calls (more of Harbor's budget) and partner shipments.
- Bad: a background charge lives in process memory; if the API restarts mid-charge, a housekeeping sweep asks the gateway for that payment id: a completed charge becomes Succeeded; one the gateway never received is settled as Declined (never charged), and the customer pays again with a new payment, by card or wallet.
- At scale: a real PSP with authorize, one partial capture of the accepted lines and webhooks (refunds only for what fails after capture); partner orders batched per seller (a partner order already maps to order lines, so this stays local); an outbox with a broker (Wolverine on PostgreSQL, later RabbitMQ or Azure Service Bus). Jobs already claim rows with `SKIP LOCKED`, so several instances can share them.

## More information

- Rules: BR-PAY-01 to BR-PAY-07, BR-ORD-01, BR-ORD-03 and the test-card table in [BUSINESS-RULES.md](../BUSINESS-RULES.md#payment)
- Domain: [CardBehaviours.cs](../../src/Lumen.Domain/Payments/CardBehaviours.cs), [PaymentTransitions.cs](../../src/Lumen.Domain/Payments/PaymentTransitions.cs), [PaymentOptions.cs](../../src/Lumen.Domain/Payments/PaymentOptions.cs), [RefundCalculator.cs](../../src/Lumen.Domain/Orders/RefundCalculator.cs), [Orders/README.md](../../src/Lumen.Domain/Orders/README.md) (state diagrams)
- Partner POSTs never retried by the HTTP pipeline: [PartnerHttpClients.cs](../../src/Lumen.Api/Partners/Http/PartnerHttpClients.cs); Kestrel's whole-order rejection: [OrderDecider.cs](../../src/StandIns/KestrelOutfitters.StandIn/Orders/OrderDecider.cs)
- Stripe, Place a hold on a payment method: <https://docs.stripe.com/payments/capture-later>; Idempotent requests: <https://docs.stripe.com/api/idempotent_requests>
