# 0002 Modular monolith in one API host; partners as separate processes

## Status

Accepted, 2026-11-20

## Context

Lumen needs a storefront and admin API, catalogue sync for two partners, checkout, card and wallet payments, and partner ordering after payment.
Launch load is small: about 8,000 visitors an hour and 3,000 orders a day (two a minute).
The hard requirements are about consistency: never oversell own stock, never charge twice, never order from a partner before the payment succeeded.
The spec requires the partner stand-ins to share no database or code with Lumen and to be reached only over HTTP, and everything must start with one command.

## Considered options

- Microservices: catalogue, ordering, payments and partner integration as services with their own databases.
- One API host plus a separate worker host for background jobs, sharing the database.
- The payment mock as its own gateway service (own process and database), the way a real PSP sits outside.
- One API host with feature folders, hosted background jobs and an in-process payment mock (chosen).

## Decision

`Lumen.Api` is the only Lumen back-end process: minimal-API feature folders (`Catalog`, `Partners`, `Identity`, `Admin`, `Carts`, `Checkout`, `Orders`, `Payments`, `Wallets`), one `DbContext`, one PostgreSQL database.
Rules with no I/O live in `Lumen.Domain` (no EF Core, no HTTP). Background work runs as hosted services in the same process: one `PartnerSyncJob` per partner, `PartnerOrderJob`, `PaymentWindowJob`, `RefundJob` and `GuestCartCleanupJob`.
The payment mock sits in the same process behind `IPaymentGateway` ([0005](0005-payments-charge-then-refund-one-partner-order-per-line.md)).

The two stand-ins are separate projects and containers, each with its own SQLite database. Architecture tests fail the build if a stand-in references any project or uses a `Lumen` namespace, and each stand-in's own tests inspect its compiled assembly. Partner base URLs are configuration, so any other implementation of the contracts can replace a stand-in.

Why: each consistency rule above needs one local transaction (reserve stock and create the order; mark the order paid and queue its partner orders). With one process and one database these are ordinary transactions; across services each would become a saga with compensation. Launch traffic gives no reason to pay that cost yet.

## Consequences

- Good: no distributed transactions and no dual writes; PostgreSQL enforces consistency ([0004](0004-checkout-consistency-the-order-is-the-price-lock.md)).
- Good: five containers, one `docker compose up`, one Lumen process to debug.
- Good: the stand-ins are real HTTP peers, so the adapters are tested against the contracts, not against shared DTOs.
- Bad: jobs share CPU, memory and the connection pool with requests, so a heavy sync can slow the storefront. Feature boundaries are folders, not enforced modules; only the stand-in boundary is tested.
- Bad: jobs and Harbor's rate limiter are per process. Sync schedules live in PostgreSQL, but a second API replica could start overlapping runs (no lock yet) and its own Harbor buckets would together exceed 60 a minute, so the API runs as one replica for now (a shared limiter and a lease fix both).
- At scale: first run the same image as a separate jobs deployment, with jobs switched off on the web replicas (`Jobs:Enabled`, already used by the tests). Then split along the feature folders where load or team ownership demands it, partner integration first (50+ partners), with an outbox and a broker between the parts. A real PSP adapter replaces the mock behind the same interface.

## More information

- Host and wiring: [Program.cs](../../src/Lumen.Api/Program.cs), [PartnersFeature.cs](../../src/Lumen.Api/Partners/PartnersFeature.cs) (one hosted job per partner), [PartnerSyncJob.cs](../../src/Lumen.Api/Partners/Sync/PartnerSyncJob.cs), [docker-compose.yml](../../docker-compose.yml)
- Isolation tests: [StandInIsolationTests.cs](../../tests/Lumen.UnitTests/Architecture/StandInIsolationTests.cs), [Harbor IsolationTests.cs](../../tests/HarborSupply.StandIn.Tests/IsolationTests.cs), [Kestrel IsolationTests.cs](../../tests/KestrelOutfitters.StandIn.Tests/IsolationTests.cs)
- Hard rules: [AGENTS.md](../../AGENTS.md)
- Martin Fowler, "MonolithFirst": <https://martinfowler.com/bliki/MonolithFirst.html>
- Shopify Engineering, "Deconstructing the Monolith": <https://shopify.engineering/deconstructing-monolith-designing-software-maximizes-developer-productivity>
