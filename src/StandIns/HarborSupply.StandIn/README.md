# Harbor Supply Co. stand-in

The sandbox of Harbor Supply Co.'s published JSON REST API. It plays the partner: it follows the contract in the
assignment spec exactly, quirks included, and adds nothing to it. Lumen Goods talks to it only over HTTP and shares
no code or data with it. Where the contract is silent, the behaviour chosen is listed under
[Interpretations of the contract](#interpretations-of-the-contract).

## What it implements

| Request | Responses |
|---|---|
| `GET /v2/catalog/items?limit=&cursor=&updated_since=` | `200 {"data":[...],"next_cursor":"..."}`, `next_cursor` is `null` on the last page; `400 INVALID_REQUEST` |
| `GET /v2/catalog/items/{item_code}` | `200` item; `404 ITEM_NOT_FOUND` |
| `POST /v2/orders` | `201 {"order_id":"H-10001","status":"ACCEPTED"}` and stock reduced; `200` same body for a reused `external_ref`; `400 INVALID_REQUEST`; `422 INVALID_ITEM`; `409 INSUFFICIENT_STOCK` with `item_code` and `available` |
| `GET /media/{item_code}/{view}.png` | the image each `media[].href` points at |

On every request: header `X-Harbor-Key: harbor-dev-key-123`, otherwise `401`. Rate limit 60 requests per minute per
key, then `429` with `Retry-After`. Every 4xx and 5xx uses the standard body
`{"error":{"code":"...","message":"..."}}` as `application/json`, framework errors included (unknown route,
wrong method, unhandled exception).

## Run it

```bash
# From the repository root. Listens on http://localhost:5081 (launch profile).
dotnet run --project src/StandIns/HarborSupply.StandIn

curl -H "X-Harbor-Key: harbor-dev-key-123" "http://localhost:5081/v2/catalog/items?limit=5"

# Container (build context = repository root). Listens on 8080.
docker build -f src/StandIns/HarborSupply.StandIn/Dockerfile -t harbor-standin:dev .
docker run --rm -p 5081:8080 -v harbor-data:/app/data harbor-standin:dev
```

The SQLite database is created and seeded on first start, and only when it holds no items, so changes survive
restarts. To start over, delete the file (`data/harbor.db` under the project locally, `/app/data/harbor.db` in the
container) or the volume.

| Setting (environment variable) | Default | Meaning |
|---|---|---|
| `Harbor__ApiKey` | `harbor-dev-key-123` | The only accepted `X-Harbor-Key` value |
| `Harbor__RequestsPerMinute` | `60` | Requests per key per 60-second window |
| `Harbor__DatabasePath` | `data/harbor.db` (container: `/app/data/harbor.db`) | SQLite file; relative paths resolve against the content root |

## Changing partner data during a demo

Partner prices and stock "can change at any time". The contract has no endpoint for that, and adding one is not
allowed, so the same binary has CLI subcommands that edit the database directly. Each change bumps the item's
`updated_at`, so Lumen sees it through `updated_since` on its next sync.

```bash
# Docker Compose
docker compose exec harbor dotnet HarborSupply.StandIn.dll set-stock HSC-10021 0
docker compose exec harbor dotnet HarborSupply.StandIn.dll set-price HSC-10021 5499
docker compose exec harbor dotnet HarborSupply.StandIn.dll set-price HSC-10021 null
docker compose exec harbor dotnet HarborSupply.StandIn.dll set-discontinued HSC-10021 true
docker compose exec harbor dotnet HarborSupply.StandIn.dll list

# Locally, from the repository root
dotnet run --project src/StandIns/HarborSupply.StandIn -- set-stock HSC-10021 0
```

Exit codes: `0` done (or nothing to change), `1` unknown item, `2` unknown command or bad arguments (usage is
printed, nothing is touched). Settings such as `--Harbor:DatabasePath=/path/harbor.db` may go before or after the
command. Any word that is not a setting selects the CLI, so a typo never starts a second server; settings alone
(for example `--urls http://+:8080`) start the server as usual.

## Seed data

48 items, 8 in each department (BAGS, TEES, OUTERWEAR, KITCHEN, CAMP, MISC). Against the contract minimums of 40
items, 3 discontinued, 2 with a null price and 5 LIMITED, the seed has 5 discontinued, 3 "price on request",
13 LIMITED (on hand 1 to 5 and 6 to 10), 6 sold out, one item with no images, and three items that share one
`updated_at` so paging has to break ties. `HSC-10021` is the spec's sample item, field for field (except the image
extension, see below).

## Tests

```bash
dotnet test tests/HarborSupply.StandIn.Tests
```

Conformance tests written from the spec before the code. They run the service in memory with a temporary SQLite
file and a fake clock (no sleeping), and assert raw status codes, headers and JSON text. Tests that cover a
conformance scenario from the requirements analysis carry its ID, e.g. `[Trait("CT", "CT-H-02")]`.

## Interpretations of the contract

| Unclear point | Behaviour chosen | Why |
|---|---|---|
| Rate-limit window | Fixed 60-second window per key value. The window opens with the key's first request; request 61 gets `429`. `Retry-After` is the whole seconds left in the window, rounded up, at least `1` | The simplest reading of "60 requests per minute". Rounding up means a client that waits exactly `Retry-After` is admitted |
| What counts toward the limit | Every authenticated request, images included. `401`s are not counted (the key is checked first); `429`s are not counted | "Every request needs the header", so images are requests like any other. Counting unauthenticated traffic would let a bad client use up the real key's budget |
| `401` error code | `UNAUTHORIZED` | The contract fixes the status and body shape but not the code |
| Other error codes not named in the contract | `400 INVALID_REQUEST`, `404 NOT_FOUND` (unknown route or image), `405 METHOD_NOT_ALLOWED`, `429 RATE_LIMITED`, `500 INTERNAL_ERROR`, always in the standard body | "Standard error body, used for every 4xx and 5xx" |
| Invalid query parameters | `400 INVALID_REQUEST` for `limit` outside 1 to 25 or not a whole number, a cursor this service did not issue, or an `updated_since` that is not ISO 8601 with a zone. Never clamped silently. An empty value (`?cursor=`) counts as not sent | A silent clamp would hide client bugs; the contract gives the valid range |
| Cursor format | Opaque base64url of JSON `{"u": updated_at, "c": item_code}` of the last item on the page, with `updated_at` at full precision. Items are ordered by `(updated_at, item_code)` and each page starts strictly after the cursor | A keyset cursor cannot skip items when others change between pages, as an offset cursor (like the sample's `{"o":25}`) can. An item changed during a pass moves to the end and is returned again later in that pass; nothing is skipped |
| `updated_since` with a cursor | Optional on later pages; the result is the same either way, because every row after the cursor is already newer than `updated_since` | Clients differ in whether they resend filters |
| `updated_since` boundary and format | Strictly after (`updated_at > updated_since`), compared with the stored full-precision `updated_at`. Accepts `2026-10-02T09:15:00Z`, fractional seconds, or an explicit offset such as `+02:00` (converted to UTC). No zone is a `400` | "Returns only items changed after it", and "UTC timestamp" |
| `updated_at` precision | Stored, filtered, sorted and put in cursors at full clock precision. The JSON shows it as in the sample: UTC, whole seconds, `Z` suffix, with the fraction cut (never rounded up). Sending a shown value back as `updated_since` can therefore return an item changed in that same second again, but never misses a later change | Matches the sample format without losing changes: truncating stored times would make two changes in one second equal, and a strictly-after filter would then skip the second one. The worst case is a duplicate, which an idempotent client absorbs |
| What bumps `updated_at` | Any field change: an order reducing stock, and every CLI change. Items are never deleted, only discontinued | The contract has no deletion signal; `updated_since` is the only change feed |
| LIMITED range | `0` is `SOLD_OUT`, `1` to `10` is `LIMITED` (with `on_hand`), more is `AVAILABLE` (no `on_hand`) | The contract names the statuses but no thresholds. 1 to 10 gives LIMITED items on both sides of Lumen's own low-stock line of 5 |
| Images | `media[].href` is an absolute path, `/media/{item_code}/{view}.png`, resolved against the service origin (not under `/v2`). Images are generated PNG placeholders (a colour per item, a shape per view), served as `image/png`. They need the key like every request | The sample's `href` is also an absolute path. PNG is generated in code with no imaging library; the extension matches the bytes, so `.png` rather than the sample's `.jpg` |
| Validation order for `POST /v2/orders` | Unreadable JSON or missing `external_ref` is `400`. Then a reused `external_ref` returns `200` with the original order, without re-validating the new body. Otherwise `400` (no lines, `qty` not a positive whole number, `ship_to` field missing, `country_iso2` not two letters; `line2` may be null or absent), then `422`, then `409` | "Returns 200 with the original order, and does not order again" reads as unconditional; 400 before 422 before 409 goes from cheapest to most stateful check |
| Does a `409` or `422` use up the `external_ref`? | No. A ref is recorded only with an accepted (`201`) order, so the same ref can be sent again after fixing the lines | "Nothing is ordered" on a 409, so there is no original order to return later |
| Several short or invalid lines | `422` names (in `message`) the first invalid line in request order. `409` reports the first short item in request order; lines for the same item are added up before the stock check | The contract has room for one `item_code`. Adding up repeated lines prevents ordering more than is on hand |
| `422` body | Standard body only (`code`, `message`); the item is named in the message | The contract adds `item_code` and `available` only to the 409 |
| `order_id` | `H-` followed by a number, starting at `H-10001` | Matches the sample's shape |
| Replay body | `200` with exactly the original `{"order_id","status"}` | "Returns 200 with the original order" |
| Request `Content-Type` | Not checked; the body must be JSON | The contract does not say |
| Concurrent orders | Orders run inside a SQLite `BEGIN IMMEDIATE` transaction, so the ref check, stock check and stock reduction are one unit, also against the CLI | Stock can never go below zero |
| Order status after ACCEPTED | None. There is no endpoint to read an order back, and none is added | "Do not add fields, endpoints or formats" |
| `item_code` matching | Exact and case-sensitive | Codes are identifiers |

## Code map

| Path | What |
|---|---|
| `Program.cs` | CLI dispatch, services, middleware order, routes |
| `HarborOptions.cs` | Settings with validation; defaults are the contract values |
| `Http/` | API key check, rate limiter, standard error body (including framework errors) |
| `Catalogue/` | List and single-item endpoints, query parsing, cursor, item JSON shape |
| `Orders/` | Order endpoint, request validation, placement in one transaction |
| `Media/` | Image endpoint and the PNG placeholder renderer |
| `Persistence/` | EF Core SQLite model, first-run seed |
| `Cli/` | `list`, `set-stock`, `set-price`, `set-discontinued` |
