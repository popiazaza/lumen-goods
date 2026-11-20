# How AI was used

AI use was allowed for this assignment, so it was used openly and throughout. This page says what it did, what a
person decided, how its output was checked, and where it was wrong.

## Tools

- **Claude Code** as the main coding agent, running several sub-agents in parallel (research, building, independent review).
- Repo instructions are vendor-neutral: [`AGENTS.md`](../AGENTS.md) (root, `src/web/`, `src/StandIns/`), read by GitHub Copilot, Codex and Claude Code alike. No tool-specific config is committed.

## Process

1. **Research**: multi-agent web research on the stack (versions and licences checked against NuGet/npm on the day), payments, partner integration, security, UX and documentation, with each claim checked against its source. The notes stayed outside the repo; their conclusions are the [ADRs](adr/README.md).
2. **Plan**: a lean plan was chosen after an independent review found the first plan two to five times too big for the task. The [assumptions](ASSUMPTIONS.md) were written down up front instead of emailing questions.
3. **Build in pull requests**: every change went through a PR with a short evidence-first description (tests, live transcripts, screenshots for UI changes), CI, and a **separate AI reviewer** that hadn't written the code. On code PRs its findings and the fixing commits are recorded in a PR comment or the PR body.

## Decided by a person, not delegated

| Decision | Where |
|---|---|
| Scope: lean plan, future features only as seams, not code | this page |
| .NET 10 LTS and PostgreSQL 18 instead of the brand-new .NET 11 / PostgreSQL 19 | [ADR 0007](adr/0007-dotnet-10-lts-and-postgresql-18.md) |
| No clarifying emails; the assumption list instead (US launch country, flat 8 % tax, …) | [ASSUMPTIONS.md](ASSUMPTIONS.md) |
| Charge then refund; one partner order per line; server retries card 0059 once with the same payment | [ADR 0005](adr/0005-payments-charge-then-refund-one-partner-order-per-line.md) |
| Vendor-neutral agent files (`AGENTS.md`), PR workflow with evidence and independent review | this page |

## How output was checked

- **Tests** named and tagged by business rule, so `dotnet test --filter-trait "BR=BR-PAY-01"` shows the proof for a rule ([BUSINESS-RULES.md](BUSINESS-RULES.md)).
- **Independent review** of each code PR by a reviewer agent briefed with the spec, not with the author's conclusions; the docs were reviewed together at the end.
- **CI** on every PR that changes code: all .NET tests on PostgreSQL via Testcontainers, Angular lint/tests/build, and a `docker compose up` smoke test. Local git hooks run the fast tests before every push.
- **Running it**: fresh-clone `docker compose up` runs, live request transcripts and desktop/mobile screenshots attached to the PRs.

## Where AI was wrong (caught before merge)

| What went wrong | Caught by | Fixed in |
|---|---|---|
| `.gitignore` rule `data/` also hid `Infrastructure/Data/` source, because git on Windows ignores case | reviewer | `fc1bff8` |
| The SPA never fetched its XSRF token, so the first POST would have failed | reviewer | `4753b60` |
| The own-stock check allowed `Reserved = NULL`, making a product show in stock but impossible to buy | reviewer | `f0cb457` |
| Products a partner removed stayed sellable | reviewer | `f21189a` |
| The partner mass-deletion guard froze all price and stock updates, not just removals | reviewer | `a987330` |
| Refunded line ids stored as JSON allowed a line to be refunded twice | reviewer | `6bb52d9` |
| Compose published ports on every interface while the demo admin password is public | reviewer | `6b52dfd` |
| `DateTimeOffset` serialised as `+00:00` where the product format requires `Z` | test | `f0cb457` |
| A commit message claimed 34 seeded Kestrel products; the seed has 37 | author check before push | amended before push |
| A partner order whose send timed out was refunded at the 30-minute deadline, though the partner may have accepted it and shipped | reviewer | `8e2ee7c` |
| An admin's confirm or reject could cross a resend already on its way to the partner | second reviewer, checking the fixes | `9a5b7c0` |
| Unpaid orders could stop expiring (and keep own stock reserved) once 50 unreadable failed payments filled every job batch | reviewer | `0ed4d3f` |
| The payment page told the customer "you have not been charged" while a failed charge could still settle | reviewer | `e2c011a` |
| Going back from Payment to Review and placing the order again created a second order holding the same stock | reviewer | `efdfb22` |
| Customers saw the partner's internal code in a refund reason (`OUT_OF_STOCK:K-7735`) | screenshots for the PR | `3b2afb9` |
| The payment diagram in the orders README did not render (a second colon in a label), and three docs disagreed with the code | writing DESIGN.md | `7e633df` |
| An XML comment containing a double hyphen broke every project's build | running the documented test command | fixed before commit |

## Guardrails

- Hard rules in [`AGENTS.md`](../AGENTS.md): no shared code with the stand-ins (enforced by architecture tests), no partner or payment I/O inside a DB transaction, `decimal` money with one rounding rule, never a partner order before payment.
- Database constraints back the code's idempotency, so a mistaken retry can't double-charge, double-order or double-refund.
