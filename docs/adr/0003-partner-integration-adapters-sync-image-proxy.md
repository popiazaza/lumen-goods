# 0003 Partner integration: anti-corruption adapters, sync, image proxy

## Status

Accepted, 2026-11-20; image token wait and cache warming added 2026-11-22

## Context

The partners' contracts are fixed and very different.
Harbor: JSON REST, opaque cursor pages of up to 25 items, `updated_since`, and an `X-Harbor-Key` limited to 60 requests a minute (then 429 with `Retry-After`).
Kestrel: one XML feed of the whole catalogue in EUR with comma decimals (`1.249,00`), German and English names and HTML in CDATA, `If-Modified-Since`, 2–4 s latency and about one 503 in ten; stock only from a CSV endpoint that takes at most 20 refs.
Both serve images only with their credentials, from hosts the browser cannot reach.
Changes must show within 15 minutes (BR-SYNC-01), a slow or down partner must not break the shop (BR-SYNC-02), and within two years the catalogue grows from about 100 to over 1,000,000 products.

## Considered options

- Read the partners live on each page view (Lumen as a proxy).
- Scheduled sync, with partner formats handled inside the catalogue code.
- Ports with one anti-corruption adapter per partner, synced into Lumen's database (chosen).
- Images: link partner URLs, copy images into Lumen storage during sync, or proxy and cache on demand (chosen).

## Decision

- **Ports.** `IPartnerCatalogSource` and `IPartnerStockSource` return products already in Lumen's terms (category, price, availability, sanitised HTML), and each adapter declares its capabilities (incremental changes, stock in the catalogue). A partner-neutral `PartnerSyncService` does the rest. An order port (`IPartnerOrderGateway`) joined them with the partner-order job.
- **Harbor.** Changes since the newest `updated_at` seen, minus a 5-minute overlap, every 60 s; a full pass every 10 min (the only way to see removals, so they show within 15). The cursor is passed back verbatim; the watermark moves only after a complete pass and each full pass resets it, so a future-dated item can't pin it. Harbor calls take tokens from two buckets that together stay under the contract: 40 a minute for catalogue and order calls, 10 for images, so a burst of image requests can't starve sync or checkout. GETs are retried and honour `Retry-After`; POSTs never are.
- **Kestrel.** The feed every 3 min with `If-Modified-Since` (304: nothing to do), unconditionally every 9 min (so a stuck `Last-Modified` can't hide changes). Prices parsed with a strict pattern and an explicit `NumberFormatInfo`. Stock read in chunks of 20 refs every 5 min and right after a feed change; a ref missing from the CSV is unknown and shown as out of stock.
- **Writes.** Upsert by (source, partner id), written only when the content hash changes. The hash covers the stored result, including the FX rate, category map and attribute synonyms, so a config change re-prices or re-maps unchanged products on the next full pass. The partner is read before any transaction opens; one `SaveChanges` writes the result. A failure keeps the last good data and shows on the admin Partner status page. Mass-deletion guard: when a catalogue arrives empty, or missing at least 10 and more than half of the listed products, the removals are held back and everything else (prices, stock, new items) still applies; an admin confirms held removals with *Confirm removals* on Partner status (a refresh with `confirmRemovals=true`). A product a partner drops is unlisted and no longer sellable.
- **Images.** `/media/{productId}/{n}` is served by Lumen, fetched through the partner's HTTP client (credentials and Harbor's bucket apply), only from origins on the partner's allow-list (`ImageOrigins`), never following redirects (SSRF guard), with credentials sent only to the partner's API origin. Image URLs carry `?v=<hash of the source URL>`, so when a partner changes an image URL the 24 h browser cache is never served stale. Successes are cached 24 h in memory (100 MB cap), failures 1 min. Partner URLs never reach the browser. A browser's request waits at most 1 s (`RateLimitWait`) for one of Harbor's image tokens; without one it gets a 503 with `Retry-After` and `no-store`, nothing is sent to Harbor and nothing is cached, and the storefront shows a placeholder until a later view. Waiting longer would hold the browser's six connections per host and stall the page's API calls. After each successful sync, the primary images of the newest 200 listed products (`WarmImageCount`) are fetched in the background, one at a time, with a token only from a full bucket, so browsers always find the burst less one.

**Business risk: Harbor's per-key limit binds at scale.** A full pass over 1,000,000 items is 40,000 pages: about 11 h at the full 60 a minute, 17 h at our 40 (the catalogue and orders bucket), before checkout checks and order POSTs take their share. Incremental sync costs the rate of change, not the catalogue size, but a 15-minute window holds 900 requests (600 at our 40), so at most about 22,500 (15,000) changed items. A full pass fits the 10-minute cycle only up to about 400 pages (about 10,000 Harbor items); beyond that, removals miss BR-SYNC-01's 15 minutes. At 100,000 orders a day, Harbor order POSTs alone would pass 60 a minute at peak hours. Lumen cannot engineer this away; it needs a higher limit, more keys or a bulk or webhook API from Harbor, and should be raised with them early.

## Consequences

- Good: a new partner is one adapter folder, one options section and a category map; the sync engine, status page and image proxy stay as they are.
- Good: browsing never calls a partner; an outage shows as stale data and an admin error, not a broken page.
- Good: the adapters rely only on the published contracts, so they work against any compliant implementation.
- Bad: the catalogue can be 15 minutes stale, so checkout re-checks partner lines ([0004](0004-checkout-consistency-the-order-is-the-price-lock.md)).
- Bad: the rate limits are per process (one API instance today), and the image cache is lost on restart.
- At scale: a global limiter with priorities (orders, then checkout checks, sync, images); full Harbor passes nightly (removals then take up to a day, so BR-SYNC-01 needs a removal feed or webhook from Harbor), with `MaxPagesPerPass` (4,000 pages today) raised; raw partner payloads stored so re-mapping needs no partner calls; images copied to object storage behind a CDN.

## More information

- Ports and engine: [IPartnerCatalogSource.cs](../../src/Lumen.Api/Partners/Ports/IPartnerCatalogSource.cs), [PartnerSyncService.cs](../../src/Lumen.Api/Partners/Sync/PartnerSyncService.cs), [ProductContent.cs](../../src/Lumen.Api/Partners/Sync/ProductContent.cs) (hash), [SyncPolicies.cs](../../src/Lumen.Api/Partners/Sync/SyncPolicies.cs) (schedule, guard), [PartnerHttpClients.cs](../../src/Lumen.Api/Partners/Http/PartnerHttpClients.cs) (resilience)
- Harbor: [HarborCatalogSource.cs](../../src/Lumen.Api/Partners/Harbor/HarborCatalogSource.cs), [HarborOptions.cs](../../src/Lumen.Api/Partners/Harbor/HarborOptions.cs), [HarborHttp.cs](../../src/Lumen.Api/Partners/Harbor/HarborHttp.cs) (token bucket)
- Kestrel: [KestrelCatalogSource.cs](../../src/Lumen.Api/Partners/Kestrel/KestrelCatalogSource.cs), [KestrelStockSource.cs](../../src/Lumen.Api/Partners/Kestrel/KestrelStockSource.cs), [KestrelPrice.cs](../../src/Lumen.Api/Partners/Kestrel/KestrelPrice.cs)
- Images: [PartnerImageProxy.cs](../../src/Lumen.Api/Partners/Media/PartnerImageProxy.cs); settings: [appsettings.json](../../src/Lumen.Api/appsettings.json)
- Tests: [SyncPolicyTests.cs](../../tests/Lumen.UnitTests/Partners/SyncPolicyTests.cs), [HarborAdapterTests.cs](../../tests/Lumen.UnitTests/Partners/HarborAdapterTests.cs), [KestrelAdapterTests.cs](../../tests/Lumen.UnitTests/Partners/KestrelAdapterTests.cs); rules BR-SYNC-01, BR-SYNC-02, BR-CAT-01 in [BUSINESS-RULES.md](../BUSINESS-RULES.md#catalogue-and-sync)
- Microsoft, Anti-corruption Layer pattern: <https://learn.microsoft.com/azure/architecture/patterns/anti-corruption-layer>; Build resilient HTTP apps: <https://learn.microsoft.com/dotnet/core/resilience/http-resilience>
