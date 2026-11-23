# Assumptions

Where the spec is silent or ambiguous, these are the choices this build makes. IDs are stable: code comments and tests cite them (e.g. `A-21`). Rows describe the behaviour of the submitted build.
Contract interpretations for the partner stand-ins live in each stand-in's README, as the spec asks; section C summarises them.

## A. Money, tax, shipping, currency

| ID | Assumption |
|---|---|
| A-01 | **Sales tax**: one configurable rate (default **8%**) applied to every order's product lines, including partner items (Lumen is the reseller). Computed only in `PricingService` (`Pricing:TaxRate`), so per-state rates or a tax provider would replace one class. Chosen over a per-state table because the spec's own sample address (Portland, OR) has no state sales tax and would show $0.00 tax in a demo |
| A-02 | **Shipping is not taxed**; tax applies to product lines only |
| A-03 | Tax is computed per line and rounded to the cent; order tax = sum of line taxes (so refunds are exact) |
| A-04 | Before an address exists, the cart shows **estimated** tax and estimated Standard shipping per seller, labelled as estimates; the review step shows the binding figures |
| A-05 | **Shipping is charged per seller shipment** (each seller ships separately). Standard **$4.99** per shipment. Express **$12.99** offered **only for Lumen's own items**, because partner order formats carry no shipping method |
| A-06 | No free-shipping threshold or discounts (offers are a planned feature) |
| A-07 | **EUR→USD** uses a **fixed rate from configuration** (default 1.0850, no markup). The rate used is stored on each order line. Production would use a daily reference rate (e.g. ECB) |
| A-08 | Convert the **unit** price, round **half away from zero** to cents (never .NET's default banker's rounding). Test: 1.249,00 EUR × 1.0850 = 1355.165 → **$1,355.17** |
| A-09 | **Any** price change between review and Place order, **up or down**, stops the order and asks the customer to re-confirm (strictest reading of "never pay a price they have not seen") |
| A-10 | A product with no valid price (Harbor `null`, unparseable Kestrel price) is shown as "Price on request" with `sellable: false` |
| A-11 | Amounts display as en-US USD with 2 decimals (`$1,355.17`) |

## B. Catalogue and display

| ID | Assumption |
|---|---|
| A-12 | Name/description language: English if present, else German as-is (no machine translation). A product with no name at all is not listed |
| A-13 | Availability is derived from the quantity whenever it is known: 0 → out of stock, 1–5 → low stock, ≥6 → in stock. Harbor AVAILABLE → in stock, quantity unknown (`null`). Harbor SOLD_OUT → 0 |
| A-14 | Kestrel stock is unknown until the stock endpoint answers; **unknown = shown as out of stock with quantity null**, so it can't be added to a cart (never shown "in stock" without evidence). Refs missing from the stock CSV are treated as unknown |
| A-15 | Kestrel `restock_date` is ignored (it isn't in the storefront format) |
| A-16 | Products in unmapped categories are hidden. Category mappings live in configuration |
| A-17 | **Listing**: discontinued, inactive and deactivated products are hidden from the catalogue, but their detail page still opens and says "No longer available" (so carts, bookmarks and order history keep working). Price-on-request items **are** listed |
| A-18 | **"Newest"** = when Lumen first saw the product (own products: created date), not last updated |
| A-19 | Attribute keys are normalised with a small dictionary then camelCase (`colour`/`Colour` → `color`) |
| A-20 | Images: Kestrel `large` preferred, else `thumb`; none → placeholder. Partner images are fetched by Lumen (with the partner's credentials) and served from Lumen URLs; the browser never calls a partner |
| A-21 | Partner HTML descriptions are sanitised server-side to a small allowlist (p, br, b, strong, i, em, ul, ol, li); everything else is stripped. The storefront shows a partner description as HTML and Lumen's own (typed by an admin) as text |
| A-22 | Search is case-insensitive over name and description. Category and seller filters allow several values; price range is inclusive; "in stock only" = sellable and in/low stock. 24 per page. Sort is included in the URL |
| A-59 | `lastUpdatedUtc` keeps the spec's meaning (when the product last changed; Harbor's own `updated_at` when it gives one). The product format adds **`lastCheckedUtc`**: when Lumen last confirmed a partner product's price and stock (the partner's last successful catalogue run, the older of that and the last stock refresh for Kestrel, or a later write to the product). The product page dates partner prices by it ("Lumen last checked … on …"); own products have none, as their price and stock are live |

## C. Partners and stand-ins

| ID | Assumption |
|---|---|
| A-23 | Harbor rate limit: fixed 60-second window per key; `Retry-After` = whole seconds to the window reset. Image requests also need the key and count toward the limit (literal reading of "every request") |
| A-24 | Harbor cursor is opaque to Lumen (the stand-in uses a keyset cursor); `updated_since` is strictly-after; any change (including stock after an order) bumps `updated_at`; items are never hard-deleted, only discontinued |
| A-25 | Harbor LIMITED = 1–10 on hand in the stand-in (so both "low" and "in stock" mappings get exercised). Invalid parameters → 400 with the standard error body |
| A-26 | A Harbor 400/409/422 does **not** use up the `external_ref`; Lumen never reuses a ref anyway |
| A-27 | Kestrel: the 2–4 s delay and 1-in-10 503 apply to every authenticated feed request (including 304s); unknown ticket → 404 empty body; ordering an inactive product → accepted, then REJECTED out of stock |
| A-28 | Kestrel delivery has no region/line2 fields: Street = "line1, line2"; the region is not sent |
| A-29 | Partner data can change during a demo via a **command-line tool inside each stand-in** (e.g. `set-stock`, `set-price`), never an HTTP endpoint |
| A-30 | Stock changes count as catalogue changes for the 15-minute rule |
| A-60 | Harbor's `media[].href` is resolved against the configured base URL, its path included: `/media/x.png` under `https://gw/harbor/` is `https://gw/harbor/media/x.png`. With compose's base URL (no path) this is the service origin |
| A-61 | Kestrel's pictures are fetched with Kestrel's credentials, so they must be on its base URL's origin (the stand-in serves them there). Other hosts can be allowed with `ImageOrigins`, but they get no credentials, so a picture host that needs Kestrel's credentials is not supported today |

## D. Checkout, payment, orders

| ID | Assumption |
|---|---|
| A-31 | Partner orders are placed **only after** payment succeeds (partners have no cancel). One partner order **per line** |
| A-32 | Payment is **charged once** at checkout; whatever a partner can't supply is **refunded automatically** |
| A-33 | Card 0059: the back end retries once with the **same payment id**; if that also fails, Lumen asks the gateway: charged → paid; not charged → the order can be paid any way; unknown → "Try again" reuses the same payment id and card (BR-PAY-05) |
| A-34 | Card 0042: the customer sees "Confirming your payment…" for up to ~20 s; leaving or refreshing the page doesn't cancel it |
| A-35 | **OTP**: one code entry per challenge; wrong code → that attempt is declined and the customer can try again (new code). Max **5** payment attempts per order. No SMS: the payment page shows a hint "test code: 123456" |
| A-36 | Card form: number, name, expiry (not in the past), CVC. Only brand + last 4 digits are stored |
| A-37 | Stolen-card decline shows the customer a generic "card declined" message; the admin sees the real reason |
| A-38 | **Wallet**: seeded customer has **$2,000.00** (enough for the >1.000 EUR item); newly registered customers start with **$500.00** (mock money). No top-up. **One payment method per payment attempt** (wallet *or* card, no split); after a declined attempt the customer may switch method |
| A-39 | Refunds go back to the original method (wallet: credit, usually within about 5 s; card: mock refund) |
| A-40 | A refund covers the line + its tax, plus that seller's shipping fee if **all** that seller's lines failed. Total refunds never exceed the amount charged |
| A-41 | Order numbers look like `LG-7K3M9QX2` (random, not sequential); partner refs are `{orderNo}-{line}` |
| A-42 | Placing the order locks its prices and reserves own stock for **15 minutes** (not on add-to-cart). The reservation is released when the order expires: the 15 minutes end or all 5 attempts are used, while no payment is in progress or Failed (a Failed charge may have gone through, so the gateway settles it first; BR-STOCK-01) |
| A-43 | **Partner never answers**: if a partner order was never accepted after retries for 30 min and the partner cannot have it (every send was rejected or never reached it) → refund that line; if a send's outcome is unknown (timeout after sending, 5xx) → flag "Needs attention" for an admin instead, no automatic refund. If Kestrel accepted it but stays undecided for 24 h → flag "Needs attention" for an admin, **no automatic refund** (the goods might still ship) |
| A-44 | Customers **can't cancel** an order after paying (partners have no cancel; support/returns are a planned feature) |
| A-45 | Lumen's own items go straight to "Confirmed" when paid; shipping/tracking is out of scope |
| A-58 | **At most 3 unpaid orders per customer** (BR-CHK-03, configurable). The spec sets no limit, but every placed order reserves own stock for 15 minutes, so one account could otherwise hold back a whole product by placing orders it never pays. An order counts while it awaits payment within its hold or a payment is in progress; it stops counting once paid, or once its hold ends even before the hold-expiry job has expired it |

## E. Cart, accounts, admin, running it

| ID | Assumption |
|---|---|
| A-46 | Mini-cart count = total quantity, not number of lines, including lines that can't be ordered (they are still in the cart). The cart summary's "Subtotal (n items)" counts only the units the subtotal covers (`totals.itemCount`) and names the lines left out |
| A-47 | Guest cart merge: same product → quantities added, capped by availability and 10 per line; registering also merges |
| A-48 | Guest cart lives 30 days after its last use, in a cookie-identified server cart |
| A-49 | A product that disappears or is deactivated stays in the cart marked "No longer available"; checkout is blocked until it's removed |
| A-50 | Registration: unique email; password of at least 8 characters with an upper-case letter, a lower-case letter, a number and a symbol (ASP.NET Identity's checks, set in one place, `Identity/PasswordRules.cs`, with plain error wording; the register page's hint and early check in `features/account/password-rules.ts` mirror it); no email verification or password reset (no email infrastructure yet) |
| A-51 | **Single launch country: US.** The spec never names a country; inferred from USD-only prices, "sales tax", the sample addresses (Portland, OR, US) and "more countries" being a planned feature. The country is a config value; addresses use US rules (the form offers the 50 states + DC; the API also accepts PR, GU, VI, AS and MP; 5-digit or ZIP+4) |
| A-52 | Admin can't hard-delete own products (orders reference them); deactivate/reactivate only. Stock edits are rejected if they'd drop below what's already reserved |
| A-53 | "Refresh now" runs a sync immediately in the background; a second click while running shows "running" |
| A-54 | "Live products" = visible in the shop; "recent errors" = sync and order errors: the last 50 are kept per partner, the API returns the newest 10, and the page shows 5 until "Show all" |
| A-55 | `docker compose up --build` includes the front-end build, migrations and seeding; no manual steps. Plain HTTP on `localhost` |
| A-56 | **Deviation from "latest stable versions"**, see [ADR 0007](adr/0007-dotnet-10-lts-and-postgresql-18.md): **.NET 10 LTS** and **PostgreSQL 18.7** on purpose: .NET 11 is GA but its PostgreSQL provider is not stable yet; adopting a brand-new major before its ecosystem catches up is a risk not worth taking for a production launch. PostgreSQL 19 was released on 2026-11-19, the day before the build started; 18.7 is its newest proven line |
| A-57 | English-only UI |
| A-62 | **Size limits the spec doesn't set** (BR-CART-03, BR-ADM-01): at most **50 different products per cart** (`Cart:MaxLines`) and 50 lines per Place order; every unit price is at most **$999,999.99** (`PriceLimits.MaxUnitPriceUsd`): the admin form refuses more for own products, and a partner price above it once converted to USD is a data error that leaves the product not sellable (at sync and in Place order's live check), so the largest order (50 lines of 10 units, tax and shipping at their configurable maximums) fits the order totals' `numeric(12,2)`. A sign-in merge keeps every line even past 50, since dropping what the customer chose is worse; such a cart takes no new products and blocks checkout, saying how many to remove (Place order would refuse more than 50 lines with a 400 anyway, and the review page shows its message). Cart changes are rate limited at 60 a minute per customer, or per client IP for a guest |
