using System.ComponentModel.DataAnnotations;
using Lumen.Domain.Catalog;

namespace Lumen.Domain.Pricing;

/// <summary>Tax and shipping settings (BR-MONEY-02, BR-SHIP-01). Bound from the <c>Pricing</c> configuration section.</summary>
public sealed class PricingOptions
{
    public const string SectionName = "Pricing";

    /// <summary>Sales tax on every product line, partner items included (Lumen is the seller of record). 0.08 = 8 %.</summary>
    [Range(typeof(decimal), "0", "1")]
    public decimal TaxRate { get; init; } = 0.08m;

    /// <summary>Fee per seller shipment on Standard shipping, in USD.</summary>
    [Range(typeof(decimal), "0", "1000")]
    public decimal StandardShippingFee { get; init; } = 4.99m;

    /// <summary>Fee for the Express shipment, in USD.</summary>
    [Range(typeof(decimal), "0", "1000")]
    public decimal ExpressShippingFee { get; init; } = 12.99m;

    /// <summary>
    /// The only seller whose shipment may go Express. Partner orders carry no shipping method, so only Lumen's own
    /// warehouse can honour Express.
    /// </summary>
    [Required, AllowedValues(ProductSources.Lumen, ProductSources.Harbor, ProductSources.Kestrel)]
    public string ExpressSource { get; init; } = ProductSources.Lumen;
}
