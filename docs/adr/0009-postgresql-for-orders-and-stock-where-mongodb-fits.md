# 0009 Data store: PostgreSQL for orders, payments and stock; where MongoDB fits

## Status

Accepted, 2026-11-22

## Context

The spec allows "a real database the application connects to, or a mock data store (for example JSON files)". Lumen's
data has two shapes:

- **Money and stock**: orders, lines, payments, refunds, wallets and own stock. Several rows change together (an order,
  its lines and its stock reservation; a payment, the order's status and its partner orders), and the business
  requirements are invariants that must hold under concurrency: never charged twice, own stock never oversold, a
  refund never larger than what was charged.
- **The catalogue**: products from many sources with partner-specific attributes, read far more than written. In two
  years it grows to over 1,000,000 products from 50+ partners, with category-specific attributes and reviews.

MongoDB is a strong document store and part of the stack many .NET teams run, so it was weighed seriously.

## Considered options

- JSON files, as the spec allows.
- MongoDB for everything.
- PostgreSQL for orders and stock, MongoDB for the catalogue.
- SQL Server.
- PostgreSQL for everything (chosen).

## Decision

PostgreSQL 18 holds all of Lumen's data. Each money and stock guarantee is a database rule that PostgreSQL enforces
under any concurrency:

| Guarantee | Database rule |
|---|---|
| A repeated Place order returns the first order | unique index on `CheckoutKey` |
| Never ordered twice at a partner | unique index on the partner order's `ExternalRef` |
| One live payment per order | partial unique index |
| Own stock never oversold | conditional `UPDATE` (on hand − reserved ≥ quantity) in the order's transaction, plus `CHECK (Reserved <= OnHand)` |
| Wallet never negative | conditional debit plus `CHECK (Balance >= 0)` |
| A line refunded at most once | unique index on the refund line |
| A stale save is refused | `xmin` row version |
| Background jobs never take the same row | `FOR UPDATE SKIP LOCKED` |

The catalogue's flexible parts are already document-shaped inside PostgreSQL: attributes and images are `jsonb`, and
search uses `pg_trgm` indexes.

Why not the others:

- **JSON files**: no transactions and no concurrency control, so "never oversold" could not hold, or be proven, under
  two simultaneous checkouts.
- **MongoDB for everything**: single-document atomic updates and multi-document transactions (on a replica set) can
  express most of these guarantees. But the cross-entity invariants above would move from declared constraints into
  application code: version fields, lease documents for job claims, and checks before each write. That is more code
  to get right, and harder to prove with tests.
- **Split stores**: own stock lives on the product and is reserved in the same transaction as the order
  (BR-STOCK-01). With the catalogue in MongoDB that becomes two stores and a saga or outbox, and two databases to
  run for about 100 products at launch.
- **SQL Server**: comparable features, with licence costs in production.

## Consequences

- Good: every money and stock rule is declared once and enforced by the database, and the integration tests prove
  them on the same engine (Testcontainers PostgreSQL 18).
- Good: one database to run, back up and reason about; on Azure it is PostgreSQL Flexible Server (DESIGN §8).
- Bad: the catalogue's read path will outgrow a relational table with `ILIKE` and `OFFSET` paging (DESIGN §7).
- **Where MongoDB fits**: when the catalogue passes about 100,000 products, or category-specific attributes need
  rich filtering, the catalogue's read side becomes a document read model. That could be MongoDB, or a search
  index, holding each product with its category-specific attributes and reviews. The sync engine and an outbox
  would feed it. Orders, payments and stock stay in PostgreSQL. The partner sync already writes through one place
  (`PartnerSyncService`), so adding that projection is a local change.

## More information

- Data model and constraints: [DESIGN.md §5](../../DESIGN.md#5-data-model); scale path: [DESIGN.md §7](../../DESIGN.md#7-scale-path)
- Checkout consistency: [ADR 0004](0004-checkout-consistency-the-order-is-the-price-lock.md); versions: [ADR 0007](0007-dotnet-10-lts-and-postgresql-18.md)
- Proof under concurrency: `tests/Lumen.IntegrationTests/Checkout/OwnStockRaceTests.cs` (50 buyers, 7 units)
