using Lumen.Api.Infrastructure.Data;
using Lumen.Api.Orders;
using Lumen.Api.Partners.Mapping;
using Lumen.Api.Partners.Ports;
using Lumen.Api.Partners.Sync;
using Lumen.Domain.Catalog;
using Lumen.Domain.Orders;
using Lumen.Domain.Payments;
using Lumen.Domain.Pricing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Lumen.Api.Checkout;

/// <summary>What Place order did.</summary>
internal abstract record PlaceOrderOutcome
{
    /// <summary>A new order: 201.</summary>
    public sealed record Created(Guid OrderId) : PlaceOrderOutcome;

    /// <summary>This checkout key already made an order for this customer (double click, retry): 200 with that order.</summary>
    public sealed record AlreadyPlaced(Guid OrderId) : PlaceOrderOutcome;

    /// <summary>What the customer reviewed is no longer true: 409, nothing created.</summary>
    public sealed record Changed(PriceChangedProblem Problem) : PlaceOrderOutcome;

    public sealed record EmptyCart : PlaceOrderOutcome;

    public sealed record ShippingMethodNotAvailable(string Message) : PlaceOrderOutcome;

    /// <summary>The key belongs to another customer's order (never happens with random keys): 409.</summary>
    public sealed record CheckoutKeyTaken : PlaceOrderOutcome;
}

/// <summary>
/// Place order: the step where what the customer reviewed becomes an order they can pay.
/// - BR-PAY-01 (a): idempotent by checkout key for this customer. A repeat (double click, retry after a timeout)
///   returns the first order; concurrent repeats are serialised on the key, so they never both create.
/// - BR-CART-03 / BR-CHK-02: lines that cannot be supplied as they are stop the order: from Lumen's data first, then
///   a live check of every partner line (one 5 s budget), before any database transaction. What the live check
///   learns (price, stock, not sold any more) is written to the product rows first, so the 409's new total, the cart,
///   the preview and a re-confirmed Place order all use the same values and converge.
/// - BR-CHK-01: the request's lines (product, quantity, unit price) and total are compared with the cart re-priced now
///   (Harbor at its live price, Kestrel at the latest feed's, Lumen's own at its current price): a line added, removed
///   or with another quantity, a price up or down, or a different total returns 409 with every difference, and nothing
///   is created or reserved. The order total is from then on the only amount a payment can charge.
/// - BR-STOCK-01: own stock is reserved in the order's transaction by conditional updates, all or nothing.
/// </summary>
internal sealed class PlaceOrderService(
    LumenDbContext db,
    CheckoutCart cart,
    PartnerAvailability availability,
    PricingService pricing,
    PaymentWindowPolicy window,
    OrderNumberGenerator numbers,
    TimeProvider time,
    ILogger<PlaceOrderService> logger)
{
    private const string ChangedLinesDetail =
        "Your cart changed, or some items can no longer be supplied as you reviewed them. Review your order again before placing it.";

    private const string PriceChangedDetail =
        "A price changed since you reviewed your order. Check the new total and confirm again.";

    private const string TotalChangedDetail =
        "The total changed since you reviewed your order. Check the new total and confirm again.";

    private const int MaxNumberAttempts = 5;

    public async Task<PlaceOrderOutcome> PlaceAsync(Guid customerId, PlaceOrderRequest request, CancellationToken cancellationToken)
    {
        var checkoutKey = request.CheckoutKey!.Value;
        if (await FindOrderAsync(customerId, checkoutKey, cancellationToken) is { } placed)
        {
            return new PlaceOrderOutcome.AlreadyPlaced(placed);
        }

        var lines = await cart.LoadAsync(customerId, cancellationToken);
        if (lines.Count == 0)
        {
            return new PlaceOrderOutcome.EmptyCart();
        }

        if (request.ShippingMethod != ShippingMethod.Standard && !lines.Any(l => l.IsOwn))
        {
            return new PlaceOrderOutcome.ShippingMethodNotAvailable(
                $"{request.ShippingMethod} shipping is only offered for Lumen Goods' own items, and this cart has none.");
        }

        var reviewed = request.Lines!.ToDictionary(l => l.ProductId!.Value);
        var changes = new List<CheckoutLineChangeDto>();

        // BR-CART-03: what Lumen's own data already rules out.
        var blocked = new HashSet<Guid>();
        foreach (var line in lines)
        {
            if (cart.Blocker(line) is { } blocker)
            {
                changes.Add(blocker);
                blocked.Add(line.Line.Id);
            }
        }

        // BR-CHK-02: partner I/O happens here, outside any transaction; what it learns is saved to the products.
        var toCheck = lines.Where(l => !l.IsOwn && !blocked.Contains(l.Line.Id)).ToList();
        var live = await availability.CheckAsync(
            [.. toCheck.Select(l => new PartnerLineToCheck(l.Product.Source, new PartnerLineCheck(
                l.Line.Id, l.Product.SourceProductId, l.Line.Quantity,
                reviewed.TryGetValue(l.Product.Id, out var seen) ? seen.UnitPrice : l.Product.PriceUsd!.Value)))],
            cancellationToken);
        await SaveLiveResultsAsync(toCheck, live, cancellationToken);
        foreach (var line in toCheck)
        {
            if (NotSupplied(line, live[line.Line.Id]) is { } notSupplied)
            {
                changes.Add(notSupplied);
            }
        }

        // BR-CHK-01: the lines and quantities reviewed against the cart now (another tab may have changed it).
        foreach (var line in lines)
        {
            var reviewedQuantity = reviewed.TryGetValue(line.Product.Id, out var r) ? r.Quantity : 0;
            if (reviewedQuantity != line.Line.Quantity)
            {
                changes.Add(cart.QuantityChange(line.Line.Id, line.Product, reviewedQuantity, line.Line.Quantity));
            }
        }

        var inCart = lines.Select(l => l.Product.Id).ToHashSet();
        var removed = reviewed.Keys.Where(id => !inCart.Contains(id)).ToList();
        if (removed.Count > 0)
        {
            var gone = await db.Products.AsNoTracking().Where(p => removed.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);
            changes.AddRange(removed.Select(id => gone.TryGetValue(id, out var product)
                ? cart.QuantityChange(null, product, reviewed[id].Quantity, 0)
                : new CheckoutLineChangeDto(null, id, "Unknown product", "", CheckoutChangeKind.CartChanged, null, null,
                    reviewed[id].Quantity, 0, null, "Removed from your cart since you reviewed it")));
        }

        // BR-CHK-01: each unit price now (the live one for partners that gave one) against the one reviewed.
        decimal? UnitPrice(CheckoutCartLine line) =>
            live.TryGetValue(line.Line.Id, out var result) && result.CurrentUnitPriceUsd is { } livePrice ? livePrice : line.Product.PriceUsd;

        foreach (var line in lines)
        {
            if (reviewed.TryGetValue(line.Product.Id, out var r) && UnitPrice(line) is { } now && now != r.UnitPrice)
            {
                changes.Add(cart.Change(line, CheckoutChangeKind.PriceChanged, from: r.UnitPrice, to: now));
            }
        }

        // A new total only makes sense when every line can be ordered as it is (only prices may differ).
        var onlyPrices = changes.All(c => c.Kind == CheckoutChangeKind.PriceChanged);
        var quote = onlyPrices
            ? pricing.Price([.. lines.Select(l => new PricingLine(l.Product.Id, l.Product.Source, UnitPrice(l)!.Value, l.Line.Quantity))],
                request.ShippingMethod)
            : null;
        if (quote is null || changes.Count > 0 || quote.GrandTotal != request.ExpectedTotal)
        {
            var detail = !onlyPrices ? ChangedLinesDetail : changes.Count > 0 ? PriceChangedDetail : TotalChangedDetail;
            logger.LogInformation(
                "Place order for customer {Customer}: {Changes} differences from the review, total {Expected} reviewed, {Total} now; nothing created",
                customerId, changes.Count, request.ExpectedTotal, quote?.GrandTotal);
            return new PlaceOrderOutcome.Changed(new PriceChangedProblem(detail, changes, quote?.GrandTotal));
        }

        return await CreateAsync(customerId, request, lines, quote, cancellationToken);
    }

    /// <summary>
    /// Write-through (BR-CHK-01, BR-CHK-02): saves each partner's live answer to its product row (live price; quantity;
    /// out of stock or no longer sold), so the 409's new total and every later read use what was just learnt. The content
    /// hash is recomputed from the stored result, so the next partner sync compares the partner's data with these
    /// values and corrects them if the partner disagrees. A row a sync changed meanwhile is left to the sync.
    /// </summary>
    private async Task SaveLiveResultsAsync(
        IReadOnlyList<CheckoutCartLine> checkedLines, IReadOnlyDictionary<Guid, PartnerLineCheckResult> live, CancellationToken cancellationToken)
    {
        var answers = checkedLines
            .Select(l => (l.Product.Id, Result: live[l.Line.Id]))
            .Where(a => a.Result.Outcome != PartnerLineCheckOutcome.Unreachable)
            .ToDictionary(a => a.Id, a => a.Result);
        if (answers.Count == 0)
        {
            return;
        }

        var ids = answers.Keys.ToList();
        var products = await db.Products.Where(p => ids.Contains(p.Id)).ToListAsync(cancellationToken);
        var now = time.GetUtcNow();
        foreach (var product in products)
        {
            var result = answers[product.Id];
            var before = ProductContent.From(product).ComputeHash();
            if (result.CurrentUnitPriceUsd is { } price && product.SourceCurrency == CurrencyConverter.Usd)
            {
                product.PriceUsd = price;
                product.SourcePrice = price;
            }

            if (result.AvailableQuantity is { } quantity)
            {
                product.Quantity = quantity;
            }
            else if (result.Outcome == PartnerLineCheckOutcome.Unavailable)
            {
                product.IsActive = false; // discontinued, unknown, or without a valid price: not sellable
            }

            var after = ProductContent.From(product).ComputeHash();
            if (after != before)
            {
                product.ContentHash = after;
                product.LastUpdatedUtc = now;
                logger.LogInformation("Live check updated {Source} product {Item}: price {Price}, quantity {Quantity}, active {Active}",
                    product.Source, product.SourceProductId, product.PriceUsd, product.Quantity, product.IsActive);
            }
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            logger.LogInformation(ex, "A partner sync updated products during Place order's live check; keeping the sync's values");
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    private CheckoutLineChangeDto? NotSupplied(CheckoutCartLine line, PartnerLineCheckResult result) => result.Outcome switch
    {
        PartnerLineCheckOutcome.Short => cart.Change(line, CheckoutChangeKind.QuantityLimited, available: result.AvailableQuantity ?? 0,
            message: $"Only {result.AvailableQuantity ?? 0} available"),
        PartnerLineCheckOutcome.Unavailable => cart.Change(line, CheckoutChangeKind.Unavailable, available: result.AvailableQuantity,
            message: result.AvailableQuantity == 0 ? "Out of stock" : "No longer available"),
        PartnerLineCheckOutcome.Unreachable => cart.Change(line, CheckoutChangeKind.TemporarilyUnavailable),
        _ => null,
    };

    private async Task<PlaceOrderOutcome> CreateAsync(
        Guid customerId, PlaceOrderRequest request, IReadOnlyList<CheckoutCartLine> lines, PriceQuote quote, CancellationToken cancellationToken)
    {
        var checkoutKey = request.CheckoutKey!.Value;
        for (var attempt = 1; ; attempt++)
        {
            var now = time.GetUtcNow();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            // BR-PAY-01 (a): concurrent requests with the same key wait here; the second then finds the first's order.
            await db.Database.ExecuteSqlAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({checkoutKey.ToString()}, 0))", cancellationToken);
            if (await FindOrderAsync(customerId, checkoutKey, cancellationToken) is { } placed)
            {
                return new PlaceOrderOutcome.AlreadyPlaced(placed);
            }

            var notReserved = await ReserveOwnStockAsync(lines, cancellationToken);
            if (notReserved.Count > 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new PlaceOrderOutcome.Changed(new PriceChangedProblem(ChangedLinesDetail, await OnlyLeftAsync(notReserved, cancellationToken), null));
            }

            var order = NewOrder(customerId, request, lines, quote, now);
            db.Orders.Add(order);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                logger.LogInformation("Order {Number} placed: {Total} USD, {Lines} lines, hold until {Hold:o}",
                    order.Number, order.Total, order.Lines.Count, order.HoldExpiresUtc);
                return new PlaceOrderOutcome.Created(order.Id);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } unique)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                db.ChangeTracker.Clear();
                if (unique.ConstraintName == "IX_Orders_Number" && attempt < MaxNumberAttempts)
                {
                    // Random order numbers collide about once in 10^12; draw again.
                    continue;
                }

                if (unique.ConstraintName == "IX_Orders_CheckoutKey")
                {
                    return await FindOrderAsync(customerId, checkoutKey, cancellationToken) is { } existing
                        ? new PlaceOrderOutcome.AlreadyPlaced(existing)
                        : new PlaceOrderOutcome.CheckoutKeyTaken();
                }

                throw;
            }
        }
    }

    /// <summary>
    /// BR-STOCK-01: one conditional update per own line (on hand − reserved ≥ quantity, still sellable), in product id
    /// order so two orders sharing products always lock them in the same order and cannot deadlock.
    /// </summary>
    /// <returns>The lines that could not be reserved; when any, the caller rolls everything back.</returns>
    private async Task<List<CheckoutCartLine>> ReserveOwnStockAsync(IReadOnlyList<CheckoutCartLine> lines, CancellationToken cancellationToken)
    {
        var failed = new List<CheckoutCartLine>();
        foreach (var line in lines.Where(l => l.IsOwn).OrderBy(l => l.Product.Id))
        {
            var quantity = line.Line.Quantity;
            var reserved = await db.Database.ExecuteSqlAsync($"""
                UPDATE "Products"
                SET "Reserved" = "Reserved" + {quantity}
                WHERE "Id" = {line.Product.Id}
                  AND "OnHand" - "Reserved" >= {quantity}
                  AND "IsActive" AND "IsListed" AND "PriceUsd" IS NOT NULL
                """, cancellationToken);
            if (reserved != 1)
            {
                failed.Add(line);
            }
        }

        return failed;
    }

    private async Task<List<CheckoutLineChangeDto>> OnlyLeftAsync(List<CheckoutCartLine> lines, CancellationToken cancellationToken)
    {
        var ids = lines.Select(l => l.Product.Id).ToList();
        var now = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);
        return [.. lines.Select(line =>
        {
            var product = now[line.Product.Id];
            if (!product.IsSellable)
            {
                return cart.Change(line, CheckoutChangeKind.Unavailable, message: "No longer available");
            }

            var available = product.AvailableQuantity ?? 0;
            return cart.Change(line, CheckoutChangeKind.QuantityLimited, available: available, message: $"Only {available} left");
        })];
    }

    private Order NewOrder(Guid customerId, PlaceOrderRequest request, IReadOnlyList<CheckoutCartLine> lines, PriceQuote quote, DateTimeOffset now)
    {
        var order = new Order
        {
            Number = numbers.Next(),
            CustomerId = customerId,
            CheckoutKey = request.CheckoutKey!.Value,
            Status = OrderTransitions.Order.Initial,
            Subtotal = quote.Subtotal,
            ShippingTotal = quote.ShippingTotal,
            TaxTotal = quote.TaxTotal,
            Total = quote.GrandTotal,
            ShipTo = request.ShippingAddress!.ToEntity(),
            ShippingMethodForOwnItems = request.ShippingMethod,
            CreatedUtc = now,
            HoldExpiresUtc = window.HoldEndsAt(now),
        };

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var product = line.Product;
            var priced = quote.Lines[i];
            var (sourcePrice, sourceCurrency, rate) = product.Source switch
            {
                // Kestrel: the latest feed's EUR price and the rate that converted it (BR-MONEY-01).
                ProductSources.Kestrel => (product.SourcePrice, product.SourceCurrency, product.FxRateToUsd),
                _ when product.SourceCurrency == "USD" => (priced.UnitPrice, "USD", (decimal?)null),
                _ => (product.SourcePrice, product.SourceCurrency, product.FxRateToUsd),
            };

            order.Lines.Add(new OrderLine
            {
                OrderId = order.Id,
                LineNumber = i + 1,
                ProductId = product.Id,
                Source = product.Source,
                SourceProductId = product.SourceProductId,
                Name = product.Name,
                PrimaryImageSourceUrl = CheckoutCart.PrimaryImageIndex(product) is { } image ? product.Images[image].SourceUrl : null,
                UnitPriceUsd = priced.UnitPrice,
                Quantity = priced.Quantity,
                LineSubtotal = priced.Subtotal,
                Tax = priced.Tax,
                SourceUnitPrice = sourcePrice,
                SourceCurrency = sourceCurrency,
                FxRateToUsd = rate,
                Status = OrderTransitions.Line.Initial,
            });
        }

        foreach (var shipment in quote.Shipments)
        {
            order.Shipments.Add(new OrderShipment { OrderId = order.Id, Source = shipment.Source, Method = shipment.Method, Fee = shipment.Fee });
        }

        return order;
    }

    private Task<Guid?> FindOrderAsync(Guid customerId, Guid checkoutKey, CancellationToken cancellationToken) =>
        db.Orders.AsNoTracking()
            .Where(o => o.CheckoutKey == checkoutKey && o.CustomerId == customerId)
            .Select(o => (Guid?)o.Id)
            .SingleOrDefaultAsync(cancellationToken);
}
