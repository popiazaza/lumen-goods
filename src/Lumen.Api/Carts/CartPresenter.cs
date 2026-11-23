using Lumen.Api.Catalog;
using Lumen.Domain.Catalog;
using Lumen.Domain.Pricing;
using Microsoft.Extensions.Options;

namespace Lumen.Api.Carts;

/// <summary>
/// Turns a cart (lines with their products loaded) into the <see cref="CartDto"/> every cart endpoint answers with:
/// each line's notices and limits from <see cref="CartRules"/>, and the totals preview from <see cref="PricingService"/>
/// at current prices with Standard shipping per seller (BR-CART-02, BR-SHIP-01, BR-MONEY-02).
/// </summary>
internal sealed class CartPresenter(
    AvailabilityPolicy policy, PricingService pricing, IOptions<CatalogOptions> catalog, IOptions<CartOptions> cartOptions)
{
    public CartDto Present(Cart? cart, HttpRequest request)
    {
        if (cart is null || cart.Lines.Count == 0)
        {
            return CartDto.Empty with { MaxLines = cartOptions.Value.MaxLines };
        }

        var baseUrl = request.PublicBaseUrl(catalog.Value);
        var lines = cart.Lines
            .OrderBy(l => l.AddedUtc).ThenBy(l => l.Id)
            .Select(line => (Line: line, Product: line.Product!, State: CartRules.Evaluate(line, line.Product!, policy)))
            .ToList();

        // Unavailable lines are left out of the totals: they cannot be bought, so they would only inflate the estimate.
        var payable = lines
            .Where(l => l.State.Unavailable is null && l.State.UnitPrice is not null)
            .Select(l => new PricingLine(l.Product.Id, l.Product.Source, l.State.UnitPrice!.Value, l.Line.Quantity))
            .ToList();
        var totals = payable.Count == 0
            ? CartTotalsDto.Zero
            : Totals(payable.Sum(l => l.Quantity), pricing.Price(payable, ShippingMethod.Standard));

        var lineDtos = lines.Select(l => ToDto(l.Line, l.Product, l.State, baseUrl)).ToList();
        return new CartDto(
            lineDtos,
            ItemCount: lines.Sum(l => l.Line.Quantity),
            totals,
            CanCheckout: lineDtos.All(l => !l.BlocksCheckout) && !CartRules.BlocksCheckoutForLines(lineDtos.Count, cartOptions.Value.MaxLines),
            MaxLines: cartOptions.Value.MaxLines);
    }

    private CartLineDto ToDto(CartLine line, Product product, CartLineState state, string baseUrl) => new(
        line.Id,
        new CartProductDto(
            product.Id,
            product.Source,
            SellerName(product.Source),
            product.Name,
            PrimaryImageUrl(product, baseUrl),
            new AvailabilityDto(ProductMapping.ToWire(state.Availability.Status), state.Availability.Quantity),
            state.Sellable),
        line.Quantity,
        state.MaxQuantity,
        state.UnitPrice,
        line.UnitPriceSeenUsd,
        state.UnitPrice * line.Quantity,
        state.Notices,
        state.BlocksCheckout,
        line.AddedUtc.UtcDateTime);

    private static CartTotalsDto Totals(int itemCount, PriceQuote quote) =>
        new(itemCount, quote.Subtotal, quote.ShippingTotal, quote.TaxTotal, quote.GrandTotal);

    private string SellerName(string source) =>
        catalog.Value.Sellers.FirstOrDefault(s => s.Source == source)?.Name ?? source;

    private static string? PrimaryImageUrl(Product product, string baseUrl)
    {
        var index = product.Images.FindIndex(i => i.IsPrimary);
        if (index < 0)
        {
            index = product.Images.Count > 0 ? 0 : -1;
        }

        return index < 0 ? null : ProductMapping.ImageUrl(baseUrl, product.Id, index, product.Images[index].SourceUrl);
    }
}
