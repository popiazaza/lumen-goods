---
name: verify-change
description: Check that a change to Lumen Goods meets the project's standards before it is committed or opened as a pull request (hard rules, warnings, tests, migrations, rule links, docs, running app, PR evidence). Use before every commit you push and before opening a PR.
---

# Verify a change

Work through the sections that apply to the diff (`git diff origin/main...HEAD`). Fix what fails; don't
report "done" with a step skipped without saying which and why.

## 1. Hard rules (from AGENTS.md)

Read the diff for each of these; each one has bitten this project before.

- [ ] Money is `decimal`, rounded only through `MoneyMath.RoundToCents` (half away from zero). No `double`, no `Math.Round` without `MidpointRounding.AwayFromZero`.
- [ ] Partner numbers are parsed with an explicit `NumberFormatInfo`, never the current culture.
- [ ] No partner or payment-gateway call while a database transaction or row lock is open. The pattern is: claim in a short transaction, call out with none open, record the result.
- [ ] No partner order before the payment succeeded.
- [ ] Stand-ins share nothing with Lumen: no project reference, DTO or database. No stand-in endpoint or field added for Lumen's convenience.
- [ ] Package versions only in `Directory.Packages.props`, never in a `.csproj`. No MediatR, AutoMapper, MassTransit or FluentAssertions (commercial licences).
- [ ] Time comes from `TimeProvider`; tests use `FakeTimeProvider`, never a fixed sleep to wait; poll for a condition with a deadline.
- [ ] A value a reviewer might ask to change is configuration (`IOptions<T>` with validation, default in `src/Lumen.Api/appsettings.json`).
- [ ] Customer-facing text never claims what the server doesn't know (for example "You have not been charged" while a charge may still settle).

## 2. Build and tests

```bash
dotnet build LumenGoods.slnx                      # must end with 0 warnings
dotnet test --solution LumenGoods.slnx            # integration tests need Docker running
cd src/web && npm run lint && npm test && npm run build
```

- [ ] A new or changed rule has tests named `BR_<AREA>_<NN>_...` and tagged `[Trait("BR", "BR-<AREA>-<NN>")]`. `dotnet test --solution LumenGoods.slnx --filter-trait "BR=BR-<AREA>-<NN>"` runs them.
- [ ] A bug fix has a test that fails without the fix.
- [ ] A race or idempotency claim has a test that runs the two sides at once, not one after the other.

## 3. Database

```bash
dotnet tool restore
dotnet ef migrations has-pending-model-changes --project src/Lumen.Api   # must say no changes
```

- [ ] An entity change has its migration, generated after merging main, so it sorts after main's latest.
- [ ] Money and stock invariants are also database constraints (`CHECK`, unique index), not only code.

## 4. Docs that must move with the code

- [ ] `docs/BUSINESS-RULES.md`: the rule row and its Tests links (use the `add-business-rule` skill).
- [ ] `docs/ASSUMPTIONS.md`: any new reading of something the spec leaves open.
- [ ] `docs/adr/`: a new ADR (MADR format) when a decision has real alternatives; never rewrite an accepted one, supersede it.
- [ ] `DESIGN.md` §9 "Where to change X": if a file named there moved or a new kind of change appears.
- [ ] `src/Lumen.Domain/Orders/README.md`: regenerate a diagram when a transition table changes. `TransitionTableTests` fails until it matches.

## 5. Run it

```bash
docker compose up --build
```

- [ ] The changed flow works through the UI at http://localhost:8080. The `demo-scenarios` skill shows how to force partner changes and every test card.
- [ ] For a UI change, run the browser tests against the stack: `cd src/web && E2E_COMPOSE=1 npm run e2e` (bash).
- [ ] For a UI change, take screenshots at desktop (1280 px) and mobile (390 px) width.

## 6. Pull request

- [ ] Title: a Conventional Commit, e.g. `fix(payments): ...`. Local hooks check commit messages and run the fast tests on push (`git config core.hooksPath .githooks` once per clone).
- [ ] Body: the four sections of `.github/pull_request_template.md` (What, Why, Evidence, Notes). Keep it short; links over prose.
- [ ] Evidence is what was actually run: commands with test counts, a request transcript, screenshots. Never "should work".
- [ ] Get a review from someone (or an agent) that did not write the change, briefed with the spec and the rules, not with your conclusions.
