# 0007 Versions: .NET 10 LTS and PostgreSQL 18

## Status

Accepted, 2026-11-20. Updated 2026-11-24: on .NET 11 now, through the upgrade path below (see "Update: upgraded on 2026-11-24").

It stays Accepted rather than Superseded: the decision is the rule, the newest set in which every component is stable and built to work with the others, and it named its own upgrade trigger. That trigger fired and the planned upgrade was made, so the rule holds; only the version numbers changed. The PostgreSQL half is unchanged.

## Context

The spec asks for the latest stable version of everything. As of 2026-11-20:

- .NET 11.0.0 (STS) and EF Core 11.0.0 shipped on 2026-11-10, but the PostgreSQL EF Core provider, `Npgsql.EntityFrameworkCore.PostgreSQL`, is still at `11.0.0-rc.3`, which pins EF Core to exactly `11.0.0-rc.3`. The newest stable provider, 10.0.3, requires EF Core `[10.0.4, 11.0.0)`.
- .NET 10 (LTS, 10.0.15) and .NET 11 (STS) both reach end of support on 2028-11-14.
- PostgreSQL 19.0 was released on 2026-11-19. PostgreSQL 18.7 (2026-11-12) is the current release of the 18 line, supported until 2030-11-14.

## Considered options

- .NET 11 + EF Core 11 + the Npgsql provider `11.0.0-rc.3`.
- .NET 11 runtime with EF Core 10 and Npgsql 10: every package stable, but mixed majors that nobody ships as a tested pairing (Identity's EF Core 11 package would pull in EF Core 11, so it too would stay on 10).
- .NET 10.0.15 LTS + EF Core 10.0.15 + Npgsql 10.0.3 (chosen).
- PostgreSQL 19.0 or 18.7 (18.7 chosen).

## Decision

We use the newest set in which every component is stable and built to work with the others: .NET SDK 10.0.404 and runtime 10.0.15, EF Core 10.0.15, Npgsql EF Core 10.0.3, PostgreSQL 18.7. Everything else was on its latest stable version when this was written (Angular 22.3, TypeScript 6.0 (the newest Angular 22.3 supports; 7.0 is out), Node 26 LTS).

.NET 11 today means shipping a release-candidate data provider or an untested mix of majors, for no extra support time (both lines end on the same day) and features we do not need. PostgreSQL 19.0 is one day old: no minor release yet, and drivers, tools and managed cloud offerings usually follow a new major by weeks or months; nothing here needs a 19 feature. Adopting a new major before its ecosystem catches up is a real production risk: the launch ends up absorbing other people's early bugs.

## Consequences

- Good: every dependency is generally available and on a supported path; .NET 10 has a year of patches behind it.
- Good: versions are pinned in a handful of files, so an upgrade is one reviewable diff.
- Bad: not literally "the latest" for .NET and PostgreSQL. Reviewers will notice; this record is the answer.
- Bad: no C# 15 (union types) or EF Core 11 features, and an upgrade is still owed.
- Upgrade path: when Npgsql EF Core 11.0.0 is generally available, one commit: `net11.0` and `LangVersion` 15 in `Directory.Build.props`, the SDK in `global.json`, package versions in `Directory.Packages.props`, image tags in the three Dockerfiles, then the full test suite. PostgreSQL 19 after its first minor release: the image tag in `docker-compose.yml` and the two integration-test databases, `LumenApiFactory.cs` and `PartnerTestHost.cs` (both pin `postgres:18.7-alpine3.24`); existing data needs `pg_upgrade` or a dump and restore (a demo volume can simply be reset with `docker compose down -v`).

## Update, 2026-11-24

`Npgsql.EntityFrameworkCore.PostgreSQL` 11.0.0 went stable at 15:37 UTC on 2026-11-20, hours after this decision, so the upgrade trigger above has fired. We stay on .NET 10 for this submission anyway:
- the switch would land in the last days before hand-in, with no time for it to settle;
- .NET 11 adds no support time (both lines end on 2028-11-14);
- nothing here needs an EF Core 11 or C# 15 feature.

The upgrade is still the one-commit change described under Consequences, and it is the first thing to do after hand-in. Meanwhile the 10.x line moved on to Npgsql EF Core 10.0.4, which is what we pin now. PostgreSQL 19 still waits for its first minor release.

## Update: upgraded on 2026-11-24

The upgrade described under Consequences is done, before hand-in after all: the owner chose not to leave it for later, since it turned out to be the small, reviewable change this record predicted and the full test suite passes on it. This replaces "we stay on .NET 10 for this submission" in the update above.

- Now: .NET SDK 11.0.100 and runtime 11.0.0 (STS, supported until 2028-11-14, the same day as .NET 10), `net11.0`, C# 15; ASP.NET Core, EF Core, Identity and the `dotnet-ef` tool 11.0.0; Npgsql EF Core 11.0.0. Every other package was already on its latest stable version. The three Dockerfiles use `sdk:11.0.100` and `aspnet:11.0.0`.
- PostgreSQL stays on 18.7: 19 still has no minor release.

What the upgrade needed beyond the pins:
- ASP.NET Core 11's request validation also walks `[AsParameters]` records. `AdminProductContext` bundles services, its `LumenDbContext` among them, so every admin product request enumerated each DbSet, and failed with a 500 once an order's navigations formed a cycle. The two service bundles, `AdminProductContext` and `AfterSignIn`, are `[SkipValidation]`; `RequestValidationScopeTests` keeps any future one honest.
- .NET 11 converts `double` to `decimal` exactly (`(decimal)54.9d` is `54.899999999999998578…`). No production code converts money from `double`; tests that passed prices as `double` `InlineData` now pass them as strings.
- `IStartupValidator` is obsolete (SYSLIB0066); the options tests call `IAsyncStartupValidator`, which `ValidateOnStart` registers too.

Reviewed, no change needed:
- Npgsql 11 reports a deadlock or serialization failure in `SaveChanges` as `DbUpdateConcurrencyException`. The handlers that catch it answer a 409 or retry, and Place order's live-price write-through leaves those rows to the partner sync; before, the request failed with a 500. The cart already treated both as conflicts.
- The OpenAPI document is now version 3.2.0, which the Scalar reference renders.
- EF Core 11 finds no pending model changes, so there is no new migration.

## More information

- Pins: [global.json](../../global.json), [Directory.Build.props](../../Directory.Build.props), [Directory.Packages.props](../../Directory.Packages.props), [docker-compose.yml](../../docker-compose.yml), [Lumen.Api Dockerfile](../../src/Lumen.Api/Dockerfile), [LumenApiFactory.cs](../../tests/Lumen.IntegrationTests/Infrastructure/LumenApiFactory.cs), [PartnerTestHost.cs](../../tests/Lumen.IntegrationTests/Partners/PartnerTestHost.cs)
- .NET release metadata: <https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json>; support policy: <https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core>
- Npgsql EF Core provider versions: <https://www.nuget.org/packages/Npgsql.EntityFrameworkCore.PostgreSQL>
- PostgreSQL versioning policy: <https://www.postgresql.org/support/versioning/>
