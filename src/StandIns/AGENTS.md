# AGENTS.md: partner stand-ins

These two services play the partners' **sandbox environments**. Their contracts are published by the
partners and are fixed. The stand-ins must behave exactly as the contract in the assignment spec says,
because Lumen may be run against someone else's implementation of the same contract.

## Rules

1. **No references to `Lumen.*`**, no shared code, no shared database. Each stand-in owns its data (SQLite file under `/app/data`, created and seeded on first run when empty).
2. **Exact contract fidelity**: same routes, query parameters, headers, auth, payload shapes, field names, number formats, status codes and error bodies as the spec. Framework defaults must not leak:
   - no `ProblemDetails`, no model-binding 400 bodies, no developer exception page;
   - JSON: `null` vs omitted exactly as specified (e.g. Harbor `on_hand` is omitted unless LIMITED, but `next_cursor: null` and `unit_price_cents: null` are present); timestamps in UTC with `Z`;
   - Kestrel 401 has an empty body; 304 has no body; the >20 refs error is `text/plain`.
3. **No extra endpoints, fields or formats.** Demo/test data changes go through a **CLI subcommand of the same binary** (e.g. `dotnet KestrelOutfitters.StandIn.dll set-stock K-7731 0`), never through HTTP.
4. **Quirks are part of the contract** (latency, failure rate, rate limits, async state changes). Implement them with options whose **defaults are the contract values**, so tests can make them deterministic (`TimeProvider`, fixed random seed, zero latency) without changing behaviour in production.
5. **Interpretations of unclear contract points** are written in the stand-in's own `README.md` (the spec requires them to live inside the stand-in).
6. **Conformance tests first**: write the tests from the spec examples before the implementation; assert raw status codes, headers and bodies.
