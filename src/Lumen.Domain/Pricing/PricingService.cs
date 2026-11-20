using Lumen.Domain.Catalog;
using Lumen.Domain.Money;

namespace Lumen.Domain.Pricing;

/// <summary>
/// Prices a cart or an order: the one place tax and shipping are calculated.
/// - BR-MONEY-02: tax = round(line subtotal × rate) per line, half away from zero; order tax = sum of line taxes.
///   Shipping is never taxed.
/// - BR-SHIP-01: one shipment per seller present. Partner shipments are always Standard; only the
///   <see cref="PricingOptions.ExpressSource"/> shipment (Lumen's own items) uses the method the customer chose.
/// </summary>
public sealed class PricingService(PricingOptions options)
{
    private readonly PricingOptions _options = DomainOptions.Validated(options);

    /// <summary>The methods to offer for this cart: Express only when it holds Lumen's own items (BR-SHIP-01).</summary>
    public IReadOnlyList<ShippingMethod> ShippingMethodsFor(IEnumerable<PricingLine> lines) =>
        lines.Any(IsOwnItem) ? [ShippingMethod.Standard, ShippingMethod.Express] : [ShippingMethod.Standard];

    /// <exception cref="ShippingMethodNotAvailableException">Express was asked for a cart with no own items.</exception>
    /// <exception cref="ArgumentException">A line has an unknown source, a quantity below 1 or a price that is not whole cents.</exception>
    public PriceQuote Price(IReadOnlyCollection<PricingLine> lines, ShippingMethod shippingMethodForOwnItems)
    {
        ArgumentNullException.ThrowIfNull(lines);
        foreach (var line in lines)
        {
            Validate(line);
        }

        if (!Enum.IsDefined(shippingMethodForOwnItems))
        {
            throw new ArgumentOutOfRangeException(nameof(shippingMethodForOwnItems));
        }

        if (shippingMethodForOwnItems != ShippingMethod.Standard && !lines.Any(IsOwnItem))
        {
            throw new ShippingMethodNotAvailableException(
                $"{shippingMethodForOwnItems} shipping is only offered for Lumen Goods' own items, and this cart has none.");
        }

        var pricedLines = lines.Select(PriceLine).ToList();
        var shipments = ProductSources.All
            .Where(source => lines.Any(l => l.Source == source))
            .Select(source => source == _options.ExpressSource
                ? new Shipment(source, shippingMethodForOwnItems, FeeFor(shippingMethodForOwnItems))
                : new Shipment(source, ShippingMethod.Standard, _options.StandardShippingFee))
            .ToList();

        return new PriceQuote(pricedLines, shipments);
    }

    private PricedLine PriceLine(PricingLine line)
    {
        // Unit prices are already whole cents, so the subtotal is exact and is not rounded again (BR-MONEY-01).
        var subtotal = line.UnitPrice * line.Quantity;
        var tax = MoneyMath.RoundToCents(subtotal * _options.TaxRate);
        return new PricedLine(line.ProductId, line.Source, line.UnitPrice, line.Quantity, subtotal, tax);
    }

    private decimal FeeFor(ShippingMethod method) => method switch
    {
        ShippingMethod.Express => _options.ExpressShippingFee,
        _ => _options.StandardShippingFee,
    };

    private bool IsOwnItem(PricingLine line) => line.Source == _options.ExpressSource;

    private static void Validate(PricingLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (!ProductSources.All.Contains(line.Source))
        {
            throw new ArgumentException($"Unknown product source '{line.Source}'.", nameof(line));
        }

        if (line.Quantity < 1)
        {
            throw new ArgumentException($"Quantity must be at least 1 (product {line.ProductId}).", nameof(line));
        }

        if (line.UnitPrice < 0 || MoneyMath.RoundToCents(line.UnitPrice) != line.UnitPrice)
        {
            throw new ArgumentException($"Unit price must be a non-negative amount in whole cents (product {line.ProductId}).", nameof(line));
        }
    }
}
