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

The first build downloads images and packages and takes a few minutes; later starts take seconds.
Everything listens on `127.0.0.1` only.

| What | URL | Credentials |
|---|---|---|
| Storefront | http://localhost:8080 | see seeded accounts |
| API reference (Scalar) | http://localhost:8080/api/docs | |
| Harbor Supply Co. sandbox | http://localhost:8081 | header `X-Harbor-Key: harbor-dev-key-123` |
| Kestrel Outfitters sandbox | http://localhost:8082 | HTTP Basic `lumen` / `kestrel-secret` |

```bash
curl -H "X-Harbor-Key: harbor-dev-key-123" "http://localhost:8081/v2/catalog/items?limit=2"
curl -u lumen:kestrel-secret "http://localhost:8082/stock?refs=K-7731,K-7732"
```

Ports can be changed with `WEB_PORT`, `HARBOR_PORT` and `KESTREL_PORT` (see `.env.example`).
Reset all data: `docker compose down --volumes`.

## Seeded accounts

| Role | Email | Password |
|---|---|---|
| Admin | `admin@lumengoods.test` | `Admin!2026` |
| Customer | `customer@lumengoods.test` | `Customer!2026` |

## Tests

```bash
dotnet test LumenGoods.slnx                # all .NET tests (integration tests need Docker running)
cd src/web && npm ci && npm test           # Angular unit tests
```

## Exact versions

| Component | Version |
|---|---|
| .NET SDK / runtime | 10.0.404 / 10.0.15 (LTS), C# 14 |
| ASP.NET Core, EF Core, Identity | 10.0.15 |
| Npgsql EF Core provider | 10.0.3 |
| PostgreSQL | 18.7 |
| Angular, Angular Material | 22.3.0 |
| TypeScript / Node.js | 6.0.3 / 26.13.1 (LTS) |
| nginx | 1.30.6 (unprivileged image) |

Every NuGet version is pinned in `Directory.Packages.props`, npm versions in `src/web/package-lock.json`.

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
| `src/StandIns/HarborSupply.StandIn` | Stand-in for Harbor Supply Co.'s JSON REST API ([contract notes](src/StandIns/HarborSupply.StandIn/README.md)) |
| `src/StandIns/KestrelOutfitters.StandIn` | Stand-in for Kestrel Outfitters' XML/CSV API ([contract notes](src/StandIns/KestrelOutfitters.StandIn/README.md)) |
| `tests/` | xUnit v3 test projects |
