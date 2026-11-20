---
name: add-business-rule
description: Add or change a business rule in Lumen Goods (pricing, tax, shipping, stock, payments, refunds, partner handling) so the rule, its single home in code, its tests and docs/BUSINESS-RULES.md stay linked by one rule ID. Use for any change to what the shop allows, charges or refunds.
---

# Add or change a business rule

Every rule has one ID (`BR-<AREA>-<NN>`, e.g. `BR-PAY-06`), one home in code, tests named and tagged with the ID,
and a row in `docs/BUSINESS-RULES.md`. A reviewer must be able to go from the spec to the row, to the code, to
the tests, and run just that rule's tests.

## 1. The row first

In `docs/BUSINESS-RULES.md`, add or edit the row in its area table (Catalogue and sync, Money, tax and shipping, Cart,
Checkout and stock, Payment, Orders and refunds, Admin):

- **Rule:** the behaviour in plain words, with the numbers taken from configuration defaults.
- **Spec requirement:** a short quote of the spec line it serves.
- **Scenarios:** Given / When / Then with concrete amounts. Compute amounts with `MoneyMath`: per line, rounded
  half away from zero, never banker's rounding.
- **Tests:** leave `*planned* · <layer>` until the tests exist.

Also update the "Requirement → rules" table if a new requirement is covered. If the rule fills a gap the spec
leaves open, add or update the assumption in `docs/ASSUMPTIONS.md`.

## 2. One home in code

- Pure rules (no I/O) go in `src/Lumen.Domain/<Area>/`, e.g. `Pricing/PricingService.cs` or `Payments/OneTimeCodePolicy.cs`.
  Rules that need the database live in the owning feature folder of `src/Lumen.Api/`.
- Put the ID in the doc comment: `/// <summary>BR-PAY-06: ...</summary>`. Never copy the logic elsewhere; call it.
- Any value a reviewer might ask to change goes in an `IOptions<T>` class with validation attributes and a default in
  `src/Lumen.Api/appsettings.json`.
- Money is `decimal`, rounded with `MoneyMath.RoundToCents`. Time comes from `TimeProvider`.
- If the rule protects money or stock under concurrency, back it with a database constraint or a conditional
  `UPDATE` too (see DESIGN.md §5 and §6), plus an EF migration:
  `dotnet ef migrations add <Name> --project src/Lumen.Api`.

## 3. Tests named and tagged with the ID

```csharp
[Fact]
[Trait("BR", "BR-PAY-06")]
public void BR_PAY_06_a_short_balance_cannot_pay_and_is_left_unchanged() { ... }
```

- Unit tests in `tests/Lumen.UnitTests/<Area>/` for the rule itself, one case per scenario in the row.
- Integration tests in `tests/Lumen.IntegrationTests/<Area>/` when the rule crosses HTTP or the database (races,
  constraints, jobs). They run the real API on PostgreSQL via Testcontainers; use the fake partners and
  `FakeTimeProvider`, never `Thread.Sleep`.
- Run one rule: `dotnet test --solution LumenGoods.slnx --filter-trait "BR=BR-PAY-06"`.

## 4. Close the loop

- Replace `*planned*` in the row's Tests cell with links to the test files, with a few words on what each proves.
- If the customer sees the rule (a message, a disabled option), cover it in the Angular page's spec in `src/web`.
- Run `dotnet test --solution LumenGoods.slnx` and `cd src/web && npm test` before pushing.
