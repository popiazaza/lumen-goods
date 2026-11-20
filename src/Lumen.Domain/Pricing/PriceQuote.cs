namespace Lumen.Domain.Pricing;

public enum ShippingMethod
{
    Standard,
    Express,
}

/// <summary>A cart or order line to price. <see cref="UnitPrice"/> is USD, already rounded to cents (BR-MONEY-01).</summary>
/// <param name="Source">A <see cref="Catalog.ProductSources"/> value: the seller that ships the item.</param>
public sealed record PricingLine(Guid ProductId, string Source, decimal UnitPrice, int Quantity);

/// <summary>A priced line. <see cref="Tax"/> is stored per line, so refunding the line refunds its exact tax.</summary>
public sealed record PricedLine(Guid ProductId, string Source, decimal UnitPrice, int Quantity, decimal Subtotal, decimal Tax)
{
    public decimal Total => Subtotal + Tax;
}

/// <summary>One seller's shipment and its fee. The fee is fixed at order time; afterwards it can only be refunded.</summary>
public sealed record Shipment(string Source, ShippingMethod Method, decimal Fee);

/// <summary>The full price of a cart or order. Totals are sums of already rounded amounts, so they need no rounding.</summary>
public sealed record PriceQuote(IReadOnlyList<PricedLine> Lines, IReadOnlyList<Shipment> Shipments)
{
    public decimal Subtotal => Lines.Sum(l => l.Subtotal);

    public decimal ShippingTotal => Shipments.Sum(s => s.Fee);

    public decimal TaxTotal => Lines.Sum(l => l.Tax);

    public decimal GrandTotal => Subtotal + ShippingTotal + TaxTotal;
}

/// <summary>The customer asked for a shipping method this cart cannot use (e.g. Express with no Lumen items). The API answers 422.</summary>
public sealed class ShippingMethodNotAvailableException(string message) : InvalidOperationException(message);
