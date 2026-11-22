# Architecture decision records

Why Lumen Goods is built the way it is, one decision per file ([how and why we keep them](0001-record-architecture-decisions.md)).

| ADR | Decision | Status |
|---|---|---|
| [0001](0001-record-architecture-decisions.md) | Record architecture decisions as short MADR files in `docs/adr/` | Accepted |
| [0002](0002-modular-monolith-in-one-api-host.md) | Modular monolith in one API host; partner stand-ins as separate processes | Accepted |
| [0003](0003-partner-integration-adapters-sync-image-proxy.md) | Partner integration: anti-corruption adapters, scheduled sync, image proxy; Harbor's rate limit as a business risk | Accepted |
| [0004](0004-checkout-consistency-the-order-is-the-price-lock.md) | Checkout consistency: the order is the price lock (re-price, unique key, atomic claim, conditional stock reservation) | Accepted |
| [0005](0005-payments-charge-then-refund-one-partner-order-per-line.md) | Payments: in-process mock gateway, charge then refund, one partner order per line | Accepted |
| [0006](0006-money-fx-tax-and-shipping.md) | Money, FX, tax and shipping: `decimal`, half away from zero, flat tax per line, shipping per seller | Accepted |
| [0007](0007-dotnet-10-lts-and-postgresql-18.md) | Versions: .NET 10 LTS and PostgreSQL 18, not .NET 11 and PostgreSQL 19 yet | Accepted |
| [0008](0008-same-origin-cookie-auth-with-xsrf-validation.md) | Same-origin cookie auth with explicit XSRF validation | Accepted |
| [0009](0009-postgresql-for-orders-and-stock-where-mongodb-fits.md) | Data store: PostgreSQL for orders, payments and stock; MongoDB for the catalogue's read side at scale | Accepted |
