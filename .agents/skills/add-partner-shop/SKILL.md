---
name: add-partner-shop
description: Add a third partner shop (a new seller whose catalogue, stock and orders come from its own API) to Lumen Goods. Use when asked to onboard a partner, add a seller, or integrate another supplier API.
---

# Add a partner shop

A partner is four adapters behind ports, plus configuration. The sync engine, checkout, partner order job,
refunds and the storefront are partner-neutral: they never need a new `if (source == ...)` branch except the
few places listed in step 4.

## 1. Pin the contract first

- Write down the partner's endpoints, auth, paging, rate limits, error format, currency and number format, and how
  it reports stock, removals and order decisions (synchronous like Harbor, or later like Kestrel).
- If the demo needs it, add a stand-in under `src/StandIns/<Name>.StandIn/`. Copy the shape of
  `HarborSupply.StandIn`: its own SQLite file, a demo CLI, conformance tests in `tests/<Name>.StandIn.Tests/`. Add both projects to `LumenGoods.slnx`,
  and raise the expected count in `Both_partner_stand_ins_exist` (`StandInIsolationTests.cs`).
- **Hard rule:** a stand-in shares no code, DTOs or database with Lumen. `tests/Lumen.UnitTests/Architecture/StandInIsolationTests.cs`
  and each stand-in's `IsolationTests.cs` enforce it. Never add an endpoint or field to a stand-in to make Lumen's
  side easier: adapt in Lumen.

## 2. Write the adapters in `src/Lumen.Api/Partners/<Name>/`

| Port (`Partners/Ports/`) | Implement | Notes |
|---|---|---|
| `IPartnerCatalogSource` | `<Name>CatalogSource` | Declare `PartnerCapabilities(IncrementalChanges, StockInCatalogue)`. Return snapshots in Lumen terms. Raise `PartnerException` for failures, never let raw `HttpRequestException` escape |
| `IPartnerStockSource` | only if stock is not in the catalogue | Batch to the partner's limit |
| `IPartnerAvailabilityCheck` | `<Name>AvailabilityCheck` | Live check at Place order, 5 s budget for all partners together |
| `IPartnerOrderGateway` | `<Name>OrderGateway` | Send `ExternalRef` (`{orderNo}-{line}`) on every retry; treat "already exists" as success; set `DecidesLater` if the partner answers later; set `OutcomeUnknown` when a send may have reached it |

Parse partner numbers with an explicit `NumberFormatInfo` (`InvariantGlobalization` is on). Map categories through
configuration (`Partners:<Name>:CategoryMap`), never in code. Pass every description through `DescriptionSanitizer`,
plain text included: the storefront renders a partner description as HTML, and search reads it with tags and
entities removed (`Product.DescriptionText`).

## 3. Register it

- `src/Lumen.Domain/Catalog/CatalogVocabulary.cs`: add the source id to `ProductSources` and `ProductSources.All`
  (the order there is the order shipments are listed).
- `src/Lumen.Api/Partners/PartnersFeature.cs`: options, a named `HttpClient` via `AddPartnerHttpClient<TOptions>`
  (resilience, auth handler), then `services.AddPartner(...)`.
- `src/Lumen.Api/Checkout/PartnerAvailability.cs` and `src/Lumen.Api/Orders/PartnerOrderGateways.cs`: register the
  availability check and the order gateway.
- `src/Lumen.Api/appsettings.json`: a `Partners:<Name>` section (its required `Name`, base URL, credentials, intervals,
  category map, resilience) and an entry in `Catalog:Sellers` (the display name).
- `docker-compose.yml`: the stand-in service and the `Partners__<Name>__*` environment variables of `api`.

## 4. Check the few partner-aware places

```bash
grep -rn "ProductSources.Kestrel\|ProductSources.Harbor" src --include=*.cs
```

- `src/Lumen.Api/Checkout/PlaceOrderService.cs`: the source currency used to re-price (EUR partners convert per unit
  at `Fx:EurToUsd`, rule BR-MONEY-01).
- `src/Lumen.Domain/Pricing/PricingOptions.cs`: `ExpressSource` allows only known sources.

## 5. Prove it

- Adapter unit tests with a stub `HttpMessageHandler`: copy `tests/Lumen.UnitTests/Partners/HarborAdapterTests.cs`.
- Sync and order integration tests: extend `tests/Lumen.IntegrationTests/Partners/FakePartners.cs` and
  `tests/Lumen.IntegrationTests/Orders/FakeOrderPartners.cs`.
- Run `dotnet test --solution LumenGoods.slnx`, then `docker compose up --build`. Open `/admin/partners` and check
  the new partner's last refresh succeeded and its products are listed.
- Add the partner's facts to `docs/ASSUMPTIONS.md` and, if a rule changes, `docs/BUSINESS-RULES.md`. Use the
  `add-business-rule` skill for that.
