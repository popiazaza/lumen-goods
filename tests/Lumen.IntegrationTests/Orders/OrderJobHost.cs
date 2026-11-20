using Lumen.Api.Catalog;
using Lumen.Api.Identity;
using Lumen.IntegrationTests.Infrastructure;
using Lumen.Api.Infrastructure.Data;
using Lumen.Api.Orders;
using Lumen.Api.Partners.Harbor;
using Lumen.Api.Partners.Kestrel;
using Lumen.Api.Payments;
using Lumen.Domain.Catalog;
using Lumen.Domain.Money;
using Lumen.Domain.Orders;
using Lumen.Domain.Payments;
using Lumen.Domain.Pricing;
using Lumen.IntegrationTests.Catalog;
using Lumen.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Lumen.IntegrationTests.Orders;

/// <summary>
/// The real API on the shared test database with both partners' order endpoints replaced by fakes (through the named
/// clients' primary handler, so credentials, the Harbor rate limiter and the resilience pipeline still run) and a fake
/// clock. Jobs are off; tests call <see cref="RunAsync"/>, which runs the partner order job for their own orders only,
/// because other tests write orders to the same database at the same time.
/// </summary>
internal sealed class OrderJobHost : IAsyncDisposable
{
    private OrderJobHost(LumenApiFactory factory, DateTimeOffset start, Action<IWebHostBuilder>? configure, FakeHarborOrders? harbor, FakeKestrelOrders? kestrel)
    {
        Harbor = harbor ?? new FakeHarborOrders();
        Kestrel = kestrel ?? new FakeKestrelOrders();
        Clock = new FakeTimeProvider(start);
        App = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Jobs:Enabled", "false");
            builder.UseSetting("Partners:Harbor:BaseUrl", "http://harbor.test");
            builder.UseSetting("Partners:Harbor:ApiKey", FakeHarborOrders.Key);
            builder.UseSetting("Partners:Harbor:Resilience:RetryBaseDelay", "00:00:00");
            builder.UseSetting("Partners:Kestrel:BaseUrl", "http://kestrel.test");
            builder.UseSetting("Partners:Kestrel:Username", FakeKestrelOrders.User);
            builder.UseSetting("Partners:Kestrel:Password", FakeKestrelOrders.Password);
            builder.UseSetting("Partners:Kestrel:Resilience:RetryBaseDelay", "00:00:00");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<TimeProvider>(Clock);
                services.AddHttpClient(HarborCatalogSource.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Harbor);
                services.AddHttpClient(KestrelCatalogSource.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Kestrel);
                services.ConfigureDbContext<LumenDbContext>(options => options.AddInterceptors(SaveFailures));
            });
            configure?.Invoke(builder);
        });
    }

    public FakeHarborOrders Harbor { get; }

    public FakeKestrelOrders Kestrel { get; }

    /// <summary>Makes saving a partner's confirmation fail, as if the database went away right after the partner accepted.</summary>
    public FailingConfirmationSave SaveFailures { get; } = new();

    public FakeTimeProvider Clock { get; }

    public WebApplicationFactory<Program> App { get; }

    public PartnerOrdersOptions Options => App.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<PartnerOrdersOptions>>().Value;

    /// <param name="harbor">Share a fake partner between hosts (two job instances talking to one partner).</param>
    public static OrderJobHost Start(LumenApiFactory factory, Action<IWebHostBuilder>? configure = null, FakeHarborOrders? harbor = null, FakeKestrelOrders? kestrel = null) =>
        new(factory, OrderingTestData.Now, configure, harbor, kestrel);

    internal PartnerOrderJob Job => App.Services.GetRequiredService<PartnerOrderJob>();

    /// <summary>One run for one partner only, for these orders only.</summary>
    internal Task<PartnerOrderRunReport> RunPartnerAsync(string partner, params Order[] orders) =>
        Job.RunPartnerOnceAsync(partner, [.. orders.Select(o => o.Id)], OrderingTestData.Ct);

    /// <summary>One run of the partner order job, for these orders only.</summary>
    internal Task<PartnerOrderRunReport> RunAsync(params Order[] orders) =>
        Job.RunOnceAsync([.. orders.Select(o => o.Id)], OrderingTestData.Ct);

    /// <summary>The seeded admin, signed in on this host (its fake clock decides whether a claim has expired).</summary>
    internal async Task<ApiSession> SignedInAdminAsync()
    {
        var session = new ApiSession(App.CreateClient());
        await session.StartAsync();
        (await session.PostAsJsonAsync("/api/v1/auth/sign-in", new SignInRequest("admin@lumengoods.test", "Admin!2026"))).EnsureSuccessStatusCode();
        await session.StartAsync();
        return session;
    }

    /// <summary>Applies a partner's answer directly, as the job would after a call (e.g. the same rejection seen twice).</summary>
    internal async Task<PartnerOrderStep> ApplyAsync(Order order, OrderLine line, Lumen.Api.Partners.Ports.PartnerOrderResult result)
    {
        await using var scope = App.Services.CreateAsyncScope();
        var work = new PartnerOrderWork(line.PartnerOrder!.Id, order.Id, line.Source, Poll: true, null,
            new Lumen.Api.Partners.Ports.PartnerOrderRequest(line.PartnerOrder.ExternalRef, line.SourceProductId, line.Quantity,
                new Lumen.Api.Partners.Ports.PartnerShipTo("A", "B", null, "C", "OR", "97201", "US")));
        return await scope.ServiceProvider.GetRequiredService<PartnerOrderProcessor>().ApplyAsync(work, result, OrderingTestData.Ct);
    }

    public ValueTask DisposeAsync() => App.DisposeAsync();
}

/// <summary>Fails the next N saves that confirm a partner order, after the partner already accepted it.</summary>
internal sealed class FailingConfirmationSave : SaveChangesInterceptor
{
    private int _failuresLeft;

    public void FailNext(int times = 1) => Interlocked.Exchange(ref _failuresLeft, times);

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        var confirming = eventData.Context?.ChangeTracker.Entries<PartnerOrder>()
            .Any(e => e.State == EntityState.Modified && e.Entity.Status == PartnerOrderStatus.Confirmed) ?? false;
        if (confirming && Interlocked.Decrement(ref _failuresLeft) >= 0)
        {
            throw new DbUpdateException("Simulated: the database connection broke while saving the partner's confirmation.");
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}

/// <summary>One line of a test order: who sells it, its id at the seller, price and quantity.</summary>
internal sealed record LineSpec(string Source, string Item, decimal UnitPrice, int Quantity)
{
    public static LineSpec Own(decimal unitPrice = 18.50m, int quantity = 1) => new(ProductSources.Lumen, Code("LG"), unitPrice, quantity);

    public static LineSpec Harbor(decimal unitPrice, int quantity = 1) => new(ProductSources.Harbor, Code("HSC"), unitPrice, quantity);

    public static LineSpec Kestrel(decimal unitPrice, int quantity = 1) => new(ProductSources.Kestrel, Code("K"), unitPrice, quantity);

    private static string Code(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 13)].ToUpperInvariant();
}

/// <summary>
/// Orders as the payment step leaves them: Paid, with own lines Confirmed, partner lines AwaitingPartner, one Pending
/// partner order per partner line (reference <c>{number}-{line}</c>) and the succeeded payment. Tax 8 % per line,
/// Standard $4.99 per seller.
/// </summary>
internal static class TestOrders
{
    public static async Task<Order> PaidOrderAsync(this LumenApiFactory factory, DateTimeOffset paidAt, params LineSpec[] lines) =>
        await factory.OrderAsync(await factory.NewCustomerAsync(), OrderStatus.Paid, paidAt, lines);

    /// <param name="status">Paid: as after a successful payment. Anything before payment: lines Pending, no payment, no partner orders.</param>
    public static async Task<Order> OrderAsync(this LumenApiFactory factory, Guid customerId, OrderStatus status, DateTimeOffset createdAt, params LineSpec[] lines)
    {
        var paid = status is OrderStatus.Paid;
        // Every line, partner lines included, points at an unlisted own product row: ordering works from the line's own
        // snapshot (Source, SourceProductId), and the partner sync tests delete all harbor/kestrel product rows between
        // tests, which an order line referencing one would block.
        var products = lines.Select(spec =>
        {
            var product = CatalogTestSupport.Own($"Order test {spec.Item}", price: spec.UnitPrice, listed: false);
            product.SourceProductId = spec.Item;
            return product;
        }).ToList();
        await factory.InsertAsync([.. products]);

        var order = new Order
        {
            Number = new OrderNumberGenerator().Next(),
            CustomerId = customerId,
            CheckoutKey = Guid.NewGuid(),
            Status = status,
            ShipTo = new ShippingAddress("Ada Lovelace", "1200 NW Naito Pkwy", "Suite 4", "Portland", "OR", "97209", "US"),
            ShippingMethodForOwnItems = ShippingMethod.Standard,
            CreatedUtc = createdAt.AddMinutes(-2),
            HoldExpiresUtc = createdAt.AddMinutes(13),
            PaidUtc = paid ? createdAt : null,
        };

        for (var i = 0; i < lines.Length; i++)
        {
            var spec = lines[i];
            var subtotal = spec.UnitPrice * spec.Quantity;
            order.Lines.Add(new OrderLine
            {
                OrderId = order.Id,
                LineNumber = i + 1,
                ProductId = products[i].Id,
                Source = spec.Source,
                SourceProductId = spec.Item,
                Name = products[i].Name,
                PrimaryImageSourceUrl = products[i].Images[0].SourceUrl,
                UnitPriceUsd = spec.UnitPrice,
                Quantity = spec.Quantity,
                LineSubtotal = subtotal,
                Tax = MoneyMath.RoundToCents(subtotal * 0.08m),
                Status = !paid ? OrderLineStatus.Pending : spec.Source == ProductSources.Lumen ? OrderLineStatus.Confirmed : OrderLineStatus.AwaitingPartner,
            });
        }

        order.Shipments.AddRange(lines.Select(l => l.Source).Distinct()
            .Select(source => new OrderShipment { OrderId = order.Id, Source = source, Method = ShippingMethod.Standard, Fee = 4.99m }));
        order.Subtotal = order.Lines.Sum(l => l.LineSubtotal);
        order.TaxTotal = order.Lines.Sum(l => l.Tax);
        order.ShippingTotal = order.Shipments.Sum(s => s.Fee);
        order.Total = order.Subtotal + order.TaxTotal + order.ShippingTotal;

        var entities = new List<object> { order };
        if (paid)
        {
            entities.Add(new Payment
            {
                OrderId = order.Id,
                Method = PaymentMethod.Card,
                Status = PaymentStatus.Succeeded,
                StatusChangedUtc = createdAt,
                Amount = order.Total,
                CardBrand = CardBrand.Visa,
                CardLast4 = "1111",
                CreatedUtc = createdAt.AddMinutes(-1),
                CompletedUtc = createdAt,
            });

            foreach (var line in order.Lines.Where(l => l.Source != ProductSources.Lumen))
            {
                line.PartnerOrder = new PartnerOrder
                {
                    OrderLineId = line.Id,
                    Source = line.Source,
                    ExternalRef = $"{order.Number}-{line.LineNumber}",
                    CreatedUtc = createdAt,
                    NextAttemptUtc = createdAt,
                };
            }
        }

        await factory.SaveAsync([.. entities]);
        return order;
    }

    /// <summary>The order as stored now, with lines, partner orders, shipments and refunds.</summary>
    public static async Task<Order> ReloadAsync(this LumenApiFactory factory, Order order)
    {
        await using var db = factory.NewContext();
        return await db.Orders.AsNoTracking()
            .Include(o => o.Lines).ThenInclude(l => l.PartnerOrder)
            .Include(o => o.Shipments)
            .Include(o => o.Refunds).ThenInclude(r => r.Lines)
            .AsSplitQuery()
            .SingleAsync(o => o.Id == order.Id, OrderingTestData.Ct);
    }

    public static OrderLine Line(this Order order, int number) => order.Lines.Single(l => l.LineNumber == number);

    public static OrderShipment Shipment(this Order order, string source) => order.Shipments.Single(s => s.Source == source);

    /// <summary>The partner's recent errors written by ordering (operation "order") that mention this text.</summary>
    public static async Task<List<string>> OrderErrorsAsync(this LumenApiFactory factory, string partner, string mentioning)
    {
        await using var db = factory.NewContext();
        return await db.PartnerSyncErrors.AsNoTracking()
            .Where(e => e.Partner == partner && e.Operation == PartnerOrderProcessor.ErrorOperation && e.Message.Contains(mentioning))
            .Select(e => e.Message)
            .ToListAsync(OrderingTestData.Ct);
    }

    /// <summary>A new customer with a password, signed in on the API (system clock) with a fresh XSRF token.</summary>
    public static async Task<(Guid Id, ApiSession Session)> SignedInCustomerAsync(this LumenApiFactory factory)
    {
        var email = $"buyer{Guid.NewGuid():N}@example.test";
        const string Password = "Shopper!2026";
        Guid id;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = new AppUser { UserName = email, Email = email };
            (await users.CreateAsync(user, Password)).Succeeded.Should().BeTrue();
            (await users.AddToRoleAsync(user, Roles.Customer)).Succeeded.Should().BeTrue();
            id = user.Id;
        }

        return (id, await factory.SignInAsync(email, Password));
    }

    public static Task<ApiSession> SignedInAdminAsync(this LumenApiFactory factory) => factory.SignInAsync("admin@lumengoods.test", "Admin!2026");

    private static async Task<ApiSession> SignInAsync(this LumenApiFactory factory, string email, string password)
    {
        var session = factory.CreateSession();
        await session.StartAsync();
        (await session.PostAsJsonAsync("/api/v1/auth/sign-in", new SignInRequest(email, password))).EnsureSuccessStatusCode();

        // Antiforgery tokens are bound to the user: fetch one for the signed-in session.
        await session.StartAsync();
        return session;
    }
}
