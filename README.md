# Lumen Goods

One online shop that sells Lumen Goods' own products and resells products from two partner shops
(Harbor Supply Co. and Kestrel Outfitters): one catalogue, one cart, one payment, one order.

> Work in progress. This README grows with the build; the final version lists every requirement and
> where to see it.

## Run it

Prerequisite: Docker (Docker Desktop on Windows/macOS). Then, from the repository root:

```bash
docker compose up --build
```

| What | URL |
|---|---|
| Storefront | http://localhost:8080 |
| API reference (Scalar) | http://localhost:8080/api/docs |
| Harbor Supply Co. sandbox | http://localhost:8081 |
| Kestrel Outfitters sandbox | http://localhost:8082 |

Ports can be changed with `WEB_PORT`, `HARBOR_PORT` and `KESTREL_PORT` (see `.env.example`).
Reset all data: `docker compose down --volumes`.

## Seeded accounts

| Role | Email | Password |
|---|---|---|
| Admin | `admin@lumengoods.test` | `Admin!2026` |
| Customer | `customer@lumengoods.test` | `Customer!2026` |

## Tests

```bash
dotnet test LumenGoods.slnx      # .NET tests (integration tests need Docker running)
cd src/web && npm test           # Angular unit tests
```

## Working on it

Changes land through pull requests; CI (`.github/workflows/ci.yml`) runs every .NET test (PostgreSQL via
Testcontainers), the Angular lint/tests/build and a `docker compose up` smoke test. Local git hooks give the
same signal earlier:

```bash
git config core.hooksPath .githooks   # once per clone: Conventional Commit check + fast pre-push tests
```

## Repository layout

| Path | What |
|---|---|
| `src/Lumen.Api` | Lumen Goods back end (ASP.NET Core 10, EF Core, PostgreSQL 18) |
| `src/Lumen.Domain` | Pure domain code: money, pricing, refunds, status rules |
| `src/web` | Storefront and admin (Angular 22) |
| `src/StandIns/HarborSupply.StandIn` | Stand-in for Harbor Supply Co.'s JSON REST API |
| `src/StandIns/KestrelOutfitters.StandIn` | Stand-in for Kestrel Outfitters' XML/CSV API |
| `tests/` | xUnit v3 test projects |
