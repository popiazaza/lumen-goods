---
name: demo-scenarios
description: Run Lumen Goods locally and force the spec's interesting cases on demand (partner price or stock changes, a partner rejecting an order, every test card) using the stand-ins' demo CLIs. Use to demo, reproduce a bug, or check a change end to end.
---

# Demo scenarios

## Start

```bash
docker compose up --build        # http://localhost:8080; ready when every service is healthy
docker compose down --volumes    # reset all data (Lumen, Harbor and Kestrel)
```

| Who | Email | Password |
|---|---|---|
| Customer (wallet $2,000.00) | `customer@lumengoods.test` | `Customer!2026` |
| Admin | `admin@lumengoods.test` | `Admin!2026` |

Partner changes reach Lumen through sync: Harbor every 60 s, Kestrel's feed every 3 min and its stock every 5 min.
To skip the wait, sign in as the admin and use **Refresh now** on `/admin/partners`.

## Force partner changes

```bash
# Harbor (item codes from `list`; price in whole cents)
docker compose exec harbor dotnet HarborSupply.StandIn.dll list
docker compose exec harbor dotnet HarborSupply.StandIn.dll set-price HSC-10025 1699
docker compose exec harbor dotnet HarborSupply.StandIn.dll set-stock HSC-10025 0
docker compose exec harbor dotnet HarborSupply.StandIn.dll set-discontinued HSC-10025 true

# Kestrel (refs from `list`; price in Kestrel's own format)
docker compose exec kestrel dotnet KestrelOutfitters.StandIn.dll list
docker compose exec kestrel dotnet KestrelOutfitters.StandIn.dll set-price K-7784 "22,95"
docker compose exec kestrel dotnet KestrelOutfitters.StandIn.dll set-stock K-7735 0
docker compose exec kestrel dotnet KestrelOutfitters.StandIn.dll set-active K-7735 N
```

| To show | Do | Expect |
|---|---|---|
| Cart notice (BR-CART-02) | Add a Kestrel item, `set-price` it, wait for the feed | "Price dropped from … to …" on the cart line, Acknowledge button |
| Price changed at Place order (BR-CHK-01) | Reach Review with a Harbor item, `set-price` it, Place order | "Your order changed" with old → new, nothing charged |
| Partner can't supply after payment (BR-ORD-03) | Place order with two Kestrel items, `set-stock` one to 0, then pay | That line: "Not supplied, refund pending", then "refunded $X" (line + its tax); the rest goes ahead |
| Removed product (BR-CAT-02) | `set-discontinued` (Harbor) or `set-active … N` (Kestrel) | Gone from the catalogue; a cart line shows "No longer available" and blocks checkout |

Place order checks Harbor's price and stock live, but only Kestrel's stock: Kestrel has no per-item price endpoint,
so a Kestrel price change is seen after the next feed sync (ADR 0004).
Kestrel decides an order 10–30 s after accepting it, so set its stock to 0 before paying, not after it confirmed.

## Test cards

The cards and what each does are listed once, in the [README](../../../README.md#test-cards).
Pay from the wallet with the same order; a refund of a wallet-paid line goes back to the wallet.

## Check what happened

- The customer sees each line's status and refunds on `/orders/<number>`.
- The admin sees payment events, partner orders (reference, ticket, attempts) and refunds on `/admin/orders/<number>`,
  and sync health on `/admin/partners`.
- `docker compose logs api` shows structured logs for sync runs, partner orders and refunds.
- The API reference is at http://localhost:8080/api/docs.
