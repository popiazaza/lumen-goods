using Lumen.Api.Catalog;
using Lumen.Domain.Catalog;
using Lumen.Domain.Pricing;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Checkout;

/// <summary>
/// The checkout preview: the customer's cart priced as an order would be (BR-MONEY-02, BR-SHIP-01), from Lumen's
/// database only. Lines that cannot be bought are listed as blockers (BR-CART-03) and left out of the totals; a line
/// above the available quantity still counts at its quantity until reduced (as in the cart). Place order re-prices and
/// checks partners live; the preview's total is what it compares against (BR-CHK-01).
/// </summary>
internal sealed class CheckoutPreviewService(CheckoutCart cart, PricingService pricing, IOptions<PricingOptions> pricingOptions)
{
    /// <exception cref="ShippingMethodNotAvailableException">Express was asked for a cart with no own items.</exception>
    public async Task<CheckoutPreviewDto> PreviewAsync(Guid customerId, ShippingMethod method, string mediaBaseUrl, CancellationToken cancellationToken)
    {
        var lines = await cart.LoadAsync(customerId, cancellationToken);
        var blockers = lines.Select(cart.Blocker).OfType<CheckoutLineChangeDto>().ToList();
        var priceable = lines
            .Where(l => blockers.All(b => b.CartLineId != l.Line.Id || b.Kind == CheckoutChangeKind.QuantityLimited))
            .ToList();
        var pricingLines = priceable.Select(l => new PricingLine(l.Product.Id, l.Product.Source, l.Product.PriceUsd!.Value, l.Line.Quantity)).ToList();

        var quote = pricing.Price(pricingLines, method);
        var options = pricing.ShippingMethodsFor(pricingLines)
            .Select(m =>
            {
                var withMethod = pricing.Price(pricingLines, m);
                var ownFee = withMethod.Shipments.FirstOrDefault(s => s.Source == pricingOptions.Value.ExpressSource)?.Fee
                    ?? (m == ShippingMethod.Express ? pricingOptions.Value.ExpressShippingFee : pricingOptions.Value.StandardShippingFee);
                return new ShippingOptionDto(m, ownFee, withMethod.ShippingTotal);
            })
            .ToList();

        var shipments = quote.Shipments.Select(shipment => new CheckoutShipmentDto(
                shipment.Source,
                cart.SellerName(shipment.Source),
                shipment.Method,
                shipment.Fee,
                [.. quote.Lines
                    .Select((priced, index) => (priced, line: priceable[index]))
                    .Where(x => x.priced.Source == shipment.Source)
                    .Select(x => new CheckoutLineDto(
                        x.line.Line.Id,
                        x.line.Product.Id,
                        x.line.Product.Name,
                        CheckoutCart.PrimaryImageIndex(x.line.Product) is { } image
                            ? ProductMapping.ImageUrl(mediaBaseUrl, x.line.Product.Id, image, x.line.Product.Images[image].SourceUrl)
                            : null,
                        x.priced.UnitPrice,
                        x.priced.Quantity,
                        x.priced.Subtotal,
                        x.priced.Tax))]))
            .ToList();

        return new CheckoutPreviewDto(shipments, quote.Subtotal, quote.ShippingTotal, quote.TaxTotal, quote.GrandTotal, method, options, blockers);
    }
}
