# Kestrel Outfitters stand-in

Plays Kestrel Outfitters' sandbox: the XML catalogue feed, CSV stock endpoint and asynchronous XML ordering API
from the assignment's published contract, quirks included. Lumen Goods talks to it only over HTTP; it shares no
code or data with Lumen. Nothing is added to the contract: no extra routes, fields, headers or formats. Demo data
changes go through CLI subcommands of the same binary.

| Route | Behaviour |
|---|---|
| `GET /feed/products.xml` | Whole catalogue, no paging, no stock. Takes 2 to 4 s, about 1 in 10 requests fail with `503`. `Last-Modified`; `If-Modified-Since` gives `304` with no body |
| `GET /stock?refs=K-7731,K-7732` | `text/csv`, `;`-separated, header `ref;units;restock_date`. At most 20 refs, otherwise `400` plain text. Unknown refs left out |
| `POST /orders` | `application/xml` `PurchaseOrder`. `202 OrderAck RECEIVED`, `409 DUPLICATE_REFERENCE` with the original ticket, `400 INVALID_REQUEST` |
| `GET /orders/{ticket}` | `OrderStatus` `RECEIVED`, then after 10 to 30 s `CONFIRMED` or `REJECTED` with `reason="OUT_OF_STOCK:<ref>"` |
| `GET /img/{file}` | The pictures the feed links to (placeholder PNGs drawn in code) |

Every request, pictures included, needs HTTP Basic `lumen` / `kestrel-secret`; otherwise `401` with an empty body.

## Run

```bash
# whole system, from the repository root
docker compose up --build

# this image alone (build context = repository root)
docker build -f src/StandIns/KestrelOutfitters.StandIn/Dockerfile -t kestrel-standin:dev .
docker run --rm -p 8082:8080 -v kestrel-data:/app/data kestrel-standin:dev

# without Docker: http://localhost:5082, database in ./data/kestrel.db under the working directory
dotnet run --project src/StandIns/KestrelOutfitters.StandIn

curl -u lumen:kestrel-secret "http://localhost:8082/stock?refs=K-7731,K-7732"
```

The container listens on 8080, runs as the non-root `app` user and keeps its SQLite file in `/app/data`
(mount a volume there). On first run, when the database is empty, it is created and seeded with 37 products:
all seven mapped groups plus `GLOVES` (unmapped), inactive products, German-only and English-only names,
products with no pictures or only a thumb, prices above 1.000,00 EUR, stock at 0 with and without a restock date,
1 to 5 units and plenty, and descriptions with unsafe HTML (`<script>`, `onerror=`, `javascript:`, `<iframe>`,
`<style>`, `onclick=`) for Lumen's sanitiser. `K-7731` and `K-7732` match the contract's samples.

## Change data during a demo (CLI)

Same binary, same database, no HTTP:

```bash
docker compose exec kestrel dotnet KestrelOutfitters.StandIn.dll list
docker compose exec kestrel dotnet KestrelOutfitters.StandIn.dll set-stock K-7731 0 2026-12-01   # restock date optional
docker compose exec kestrel dotnet KestrelOutfitters.StandIn.dll set-price K-7731 "1.249,00"     # Kestrel's format only
docker compose exec kestrel dotnet KestrelOutfitters.StandIn.dll set-active K-7731 N
```

To force a `REJECTED` order: place it, then `set-stock <ref> 0` within the 10 to 30 s before it is decided.
`set-price` and `set-active` move the feed's `Last-Modified`; `set-stock` does not (stock is not in the feed).
Locally: `dotnet run --project src/StandIns/KestrelOutfitters.StandIn -- set-stock K-7731 0`.

## Configuration

Section `KestrelOutfitters` (environment: `KestrelOutfitters__FeedFailureRate=0`). Every default is the contract
value; overrides exist so tests and demos can make the quirks deterministic.

| Key | Default | |
|---|---|---|
| `Username`, `Password` | `lumen`, `kestrel-secret` | Basic credentials |
| `DatabasePath` | `data/kestrel.db` (`/app/data/kestrel.db` in the image) | SQLite file, created and seeded when empty |
| `PublicBaseUrl` | empty | Base of picture URLs; empty means the incoming request's scheme and host |
| `FeedLatencyMin`, `FeedLatencyMax` | `00:00:02`, `00:00:04` | Uniform feed delay |
| `FeedFailureRate` | `0.1` | Share of feed requests answered with `503` |
| `OrderDecisionDelayMin`, `OrderDecisionDelayMax` | `00:00:10`, `00:00:30` | Uniform time an order stays `RECEIVED` |
| `OrderDecisionPollInterval` | `00:00:01` | How often the background service decides due orders |
| `RandomSeed` | empty | Fixes the sequence of delays and failures |

## Interpretations of the contract

Where the contract is silent, this is the behaviour chosen. Lumen's adapter does not depend on any of it.

| Unclear point | Behaviour chosen | Why |
|---|---|---|
| Which feed requests are slow and fail | Every authenticated feed request, `304`s included: delay first, then the 1-in-10 `503`. A `401` is immediate. Other routes are neither slow nor failing | The contract names the feed endpoint only; authentication is checked before any work |
| `503` body | Empty, no `Retry-After` | The contract defines no body or header; adding one would be a new format |
| `generated` attribute | When the current catalogue version was produced (equals `Last-Modified`), rendered with a fixed `+01:00` offset like the sample | An unchanged catalogue gives a byte-identical document; the offset makes Lumen convert to UTC rather than assume it |
| `Last-Modified` granularity | Whole seconds (HTTP-date). Each price or active change moves it to a strictly later second; stock changes do not move it. `304` when `If-Modified-Since` >= `Last-Modified`; an unparseable date is ignored | A change in the same second as Lumen's last fetch must not be hidden by a `304` |
| `304` response | No body, `Last-Modified` repeated | RFC 9110 allows the validator on a `304`; the contract asks only for no body |
| Feed `Content-Type` | `application/xml; charset=utf-8`; declaration exactly as the sample (`encoding="UTF-8"`) | Not specified; standard type for XML |
| Product with no pictures | No `<Pictures>` element at all (`Specs` is always present) | "Some have none"; the stricter reading for a parser |
| Picture URLs and files | `{request scheme and host}/img/k7731_t.png` and `_l.png` (or `PublicBaseUrl`), PNG placeholders. Unknown or unpublished pictures: `404`, empty body | The sample's `kestrel.example` does not resolve; the contract says the stand-in serves its own |
| Auth on pictures and unknown paths | Required ("every request"). Authenticated unknown path: `404`, empty body | Literal reading; Lumen proxies pictures server-side with the credentials |
| `WWW-Authenticate` on `401` | `Basic realm="Kestrel Outfitters", charset="UTF-8"`; body stays empty | RFC 9110 requires the challenge on a `401`; it is a standard header, not a new format |
| `/stock` input | The 20 limit counts every comma-separated entry as sent, before trimming, removing duplicates or dropping empty entries: 21 entries is a `400` plain text even if they name fewer refs (a repeat or a trailing comma counts). Within the limit, entries are trimmed, empty ones dropped and duplicates answered once (case-sensitive). Missing or empty `refs`: `400` plain text. Repeated `refs` parameters are combined | "At most 20 refs per request" read strictly, as a real partner might: a client that pads or repeats past 20 finds out against the stand-in, not in production |
| `/stock` output | Rows in request order, CRLF line ends, `Content-Type: text/csv` exactly, `restock_date` empty when unknown. Inactive products are still listed, with their physical units | RFC 4180 line ends; the contract excludes only unknown refs, and `/stock` reports what is on the shelf |
| Stock while an order is `RECEIVED` | Not held. Orders due for a decision are settled before `/stock` and `/orders/{ticket}` answer, and by a background tick every second | "Rejected when any line exceeds stock at that moment"; reads never lag the clock, stock moves even if nobody polls |
| `POST /orders` validation | `400 INVALID_REQUEST` for: `Content-Type` other than `application/xml` (parameters allowed), XML that is not well formed or has a DTD, wrong root, missing or blank `reference`, no `Line`, `qty` not a whole number above 0, missing or blank `Delivery` field, unknown refs (all listed in `message`). Unknown extra elements are ignored | "Malformed requests or unknown refs" |
| `400` versus `409` | Validation first, then the duplicate check. A `400` does not use up the reference. A reused reference gets `409` with the original ticket whatever the new payload is | The reference is the idempotency key; a rejected request was never received |
| Repeated `Line` for one ref | Quantities summed | One ref, one stock check |
| Ordering an inactive product | Accepted with `202` (the ref is known, and the contract's `400` is only for malformed requests or unknown refs), then `REJECTED` with `OUT_OF_STOCK:<ref>` when decided. The decider counts an inactive product as 0 units available to order, while `/stock` still reports its physical units | A project assumption. An inactive product cannot be supplied, and the contract gives `OUT_OF_STOCK:K-7731` only as "a reason such as", so reusing it fits without inventing a reason code Lumen could not know. `/stock` stays a plain report of units on hand; the feed's `Active` is what tells Lumen not to sell |
| Several short lines | Whole order `REJECTED`; `reason` names the first short line in request order, as in the sample. No stock moves | The contract has a single-ref example and no partial confirmation |
| Decision timing | Each order gets its own uniform 10 to 30 s at receipt. Due orders are decided in order of their decision time (ties by receipt), not in order of receipt, so an order received later with a shorter wait can take the last units first | "At that moment" is each order's own moment; the receipt order is not what decides who gets the stock |
| Unknown ticket | `404`, empty body | The contract defines no error for it |
| Tickets | `KT-5521`, `KT-5522`, ... in order of receipt | Format of the sample |
| Order response bodies | The bare element, no XML declaration (as in the samples), `application/xml; charset=utf-8`. The serialiser writes `<OrderAck ... />` with a space before `/>` | Same XML; values are always escaped by `XElement` |
| Other failures | Wrong method: `405`, unhandled error: `500`, both with empty bodies. Never a `ProblemDetails` or developer error page | The contract has no generic error format |

## Tests

`tests/KestrelOutfitters.StandIn.Tests`: conformance tests written from the contract, run in memory on a temporary
SQLite file with a fake clock (no sleeping). They assert raw status codes, headers and bodies.

```bash
dotnet test tests/KestrelOutfitters.StandIn.Tests
```
