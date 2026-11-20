# AGENTS.md

Instructions for AI coding agents (GitHub Copilot, Codex, Claude Code, ...) and humans working in this repo.
Nested `AGENTS.md` files in `src/web/` and `src/StandIns/` add rules for those areas.

## What this is

Lumen Goods: an online shop that sells its own products plus products from two partner shops,
in one cart, with one payment and one order. Four deliverables in one repo:

| Path | What |
|---|---|
| `src/Lumen.Api` | The Lumen Goods back end (ASP.NET Core minimal APIs, EF Core, PostgreSQL, hosted background jobs, in-process payment mock) |
| `src/Lumen.Domain` | Pure domain code: money, pricing, refunds, status transitions. No EF Core, no HTTP |
| `src/web` | The storefront and admin (Angular) |
| `src/StandIns/HarborSupply.StandIn` | Stand-in for Harbor Supply Co.'s JSON REST API |
| `src/StandIns/KestrelOutfitters.StandIn` | Stand-in for Kestrel Outfitters' XML feed, CSV stock and async XML ordering API |
| `tests/` | xUnit v3 test projects, one per source project area |
| `docs/` | ADRs, business rules, AI usage |

## Commands

```bash
docker compose up --build          # run everything (http://localhost:8080)
dotnet build LumenGoods.slnx       # build all .NET projects
dotnet test LumenGoods.slnx        # all .NET tests (integration tests need Docker running)
dotnet test --filter "BR=BR-PAY-01"  # tests for one business rule
cd src/web && npm test             # Angular unit tests
```

## Stack (pinned)

.NET SDK 10.0.404 (`global.json`), every NuGet version in `Directory.Packages.props` (central package management: never put a version in a `.csproj`).
PostgreSQL 18.7. Angular 22.3, TypeScript 6.0, Node 26 LTS.

## Hard rules

- **Partner stand-ins share nothing with Lumen**: no project references, no shared DTOs, no shared database. Lumen talks to them only over HTTP.
- **Partner contracts are fixed**: never add endpoints, fields or formats to a stand-in to make Lumen's side easier. All adapting happens in `Lumen.Api/Partners/*`.
- **Money is `decimal`**, rounded half away from zero (`MidpointRounding.AwayFromZero`), always serialised with exactly 2 decimals. Never `double`, never banker's rounding.
- **Never parse partner numbers with a culture** (`InvariantGlobalization` is on). Use an explicit `NumberFormatInfo`.
- **No partner or payment I/O inside a database transaction.**
- **Never place a partner order before the payment succeeded.**
- **Business rules live in one place each** (a policy/service in `Lumen.Domain` or a module), carry their rule ID in a comment, and have tests named and tagged with that ID: `BR_PAY_01_...` + `[Trait("BR", "BR-PAY-01")]`. The catalogue is `docs/BUSINESS-RULES.md`.
- **Configuration over code** for anything a reviewer might ask to change: category maps, thresholds, tax rate, shipping fees, FX rate, timeouts. Use `IOptions<T>` with validation.
- **Use `TimeProvider`** wherever time matters, so tests never sleep.

## Conventions

- C#: file-scoped namespaces, minimal APIs with `TypedResults`, one route group per feature, `ProblemDetails` (RFC 9457) for Lumen API errors (not for stand-ins: they use their partner's error formats).
- Feature folders inside `Lumen.Api` (`Catalog`, `Carts`, `Checkout`, `Orders`, `Payments`, `Wallets`, `Partners/Harbor`, `Partners/Kestrel`, `Admin`, `Identity`). A folder (and namespace) is never named like a class in it: `Carts` holds `Cart`, `Wallets` holds `Wallet`.
- No MediatR, AutoMapper, MassTransit or FluentAssertions (commercial licences); hand-written handlers and mapping.
- Tests: xUnit v3 + AwesomeAssertions. Integration tests use Testcontainers PostgreSQL.
- Commits: Conventional Commits with a module scope, e.g. `feat(stand-in-harbor): cursor paging`.
- Comments explain *why*, not what. No emojis in code, logs or docs.
