# 0006 Money, FX, tax and shipping

## Status

Accepted, 2026-11-20

## Context

All prices are shown and charged in USD; Kestrel's prices are EUR strings such as `1.249,00`.
Orders must include shipping and sales tax, but the spec gives no rates or rules.
Refunds happen per line after payment ([0005](0005-payments-charge-then-refund-one-partner-order-per-line.md)), so each line's amounts must be exact on their own.
The containers run with invariant globalization, and .NET's default `Math.Round` is banker's rounding.

## Considered options

- Amounts as `decimal`, as integer cents, or through a money library (NodaMoney).
- EUR converted per unit, per line or on the order total; at a configured rate or a live one.
- Sales tax as one configurable rate, a per-state table, or a tax service.
- Shipping as one fee per order, or one per seller shipment.

## Decision

- **Money** is `decimal` USD, rounded in one place (`MoneyMath.RoundToCents`, half away from zero), stored with fixed precision (`numeric(12,2)` for prices) and always serialised with two decimals (`49.90`, never `49.9`). Kestrel prices are parsed with a strict pattern and an explicit `NumberFormatInfo`, never a culture.
- **FX** (BR-MONEY-01): EUR to USD per unit at the configured `Fx:EurToUsd` (default 1.0850), rounded to the cent; a line is unit price times quantity with no further rounding. The rate is stored on the product and copied onto each order line when the order is placed. 1.249,00 EUR becomes 1355.165, then $1,355.17 (banker's rounding would give .16).
- **Tax** (BR-MONEY-02): one configurable rate (default 8 %) on every product line, partner items included, because Lumen is the seller of record. Computed and rounded per line; the order's tax is the sum, so a refunded line's tax is exact. Shipping is not taxed. Not per state: the spec's sample address is in Portland, Oregon, which has no sales tax, so a per-state demo would show $0.00 tax.
- **Shipping** (BR-SHIP-01): one shipment per seller in the order, Standard $4.99 each. Express ($12.99) only for Lumen's own shipment, because partner order payloads carry no shipping method. The fee is stored per shipment and can afterwards only be refunded.
- Tax and shipping are calculated only in `PricingService`, which cart, review and order all use.

## Consequences

- Good: one rounding rule and one pricing service, so cart, review, order and refund cannot disagree; the rule examples are unit tests.
- Good: a new FX rate re-prices partner products through the content hash ([0003](0003-partner-integration-adapters-sync-image-proxy.md)) and carts will flag the new price; placed orders keep the rate they were charged at.
- Bad: a flat 8 % is wrong for most real US addresses (state and local rates, marketplace rules). Right for a demo, not for launch.
- Bad: a fixed rate drifts from the market, and Lumen carries the currency risk on Kestrel items.
- Bad: a cart with items from all three sellers pays three shipping fees ($14.97 on Standard), which customers may question.
- At scale: a tax service priced by destination address; a daily reference rate (for example the ECB's) plus a margin, snapshotted per order; a currency code on every amount before selling outside the US.

## More information

- Code: [MoneyMath.cs](../../src/Lumen.Domain/Money/MoneyMath.cs), [TwoDecimalJsonConverter.cs](../../src/Lumen.Api/Infrastructure/Json/TwoDecimalJsonConverter.cs), [ProductConfiguration.cs](../../src/Lumen.Api/Catalog/ProductConfiguration.cs) (precision), [KestrelPrice.cs](../../src/Lumen.Api/Partners/Kestrel/KestrelPrice.cs), [CurrencyConverter.cs](../../src/Lumen.Api/Partners/Mapping/CurrencyConverter.cs), [PricingService.cs](../../src/Lumen.Domain/Pricing/PricingService.cs), [PricingOptions.cs](../../src/Lumen.Domain/Pricing/PricingOptions.cs)
- Tests: [MoneyMathTests.cs](../../tests/Lumen.UnitTests/Money/MoneyMathTests.cs), [PricingServiceTests.cs](../../tests/Lumen.UnitTests/Pricing/PricingServiceTests.cs); rules BR-MONEY-01, BR-MONEY-02, BR-SHIP-01 in [BUSINESS-RULES.md](../BUSINESS-RULES.md#money-tax-and-shipping)
- .NET `MidpointRounding`: <https://learn.microsoft.com/dotnet/api/system.midpointrounding>
- ECB euro foreign exchange reference rates: <https://www.ecb.europa.eu/stats/policy_and_exchange_rates/euro_reference_exchange_rates/html/index.en.html>
