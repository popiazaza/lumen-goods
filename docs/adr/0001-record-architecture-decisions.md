# 0001 Record architecture decisions

## Status

Accepted, 2026-11-20

## Context

Lumen Goods is built on several parallel branches, much of it with AI agents, and the repository is read by people who were not there when the decisions were made.
The reasons behind the structure (why one host, why charge then refund, why these versions) are not visible in the code, and they are the first questions a reviewer or a new developer asks.
They need to live next to the code, change through pull requests, and be short enough that people actually read them.

## Considered options

- No records: reasons only in pull request descriptions and commit messages.
- One long design document holding every decision.
- Michael Nygard's original ADR format.
- MADR 4, minimal variant: one Markdown file per decision (chosen).

## Decision

We write records based on MADR 4 minimal, one decision per file, in `docs/adr/`, named `NNNN-short-title.md` and listed in [README.md](README.md).
Sections: Status, Context, Considered options, Decision, Consequences (good, bad, and what we would change at scale) and More information (code paths, business rule IDs, a few public sources). One record fits on about one screen.

An accepted record is not rewritten. A changed decision gets a new record, and the old one's status becomes "Superseded by NNNN". Small corrections, such as a moved file path, are fine.

Records say *why*. [BUSINESS-RULES.md](../BUSINESS-RULES.md) says *what* the system must do (rule IDs such as BR-PAY-01, each with its tests); records cite rule IDs instead of restating them.

## Consequences

- Good: a decision is reviewed in the same pull request as the code it shapes, and links to that code.
- Good: short, separate files are cheap to read; a reviewer can open only the ones they care about.
- Bad: records can drift from the code. Links to concrete files make drift visible in review, but nothing checks it automatically.
- Bad: records written before their code exists (checkout, payments) describe intent and link rules, not code, until the code lands.
- At scale: a "Proposed" status reviewed by the teams affected, and a CI check that every record is in the index.

## More information

- MADR: <https://adr.github.io/madr/>
- Michael Nygard, "Documenting Architecture Decisions" (2011): <https://cognitect.com/blog/2011/11/15/documenting-architecture-decisions>
- Repository conventions: [AGENTS.md](../../AGENTS.md)
