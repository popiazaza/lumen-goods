using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Lumen.Api.Carts;
using Lumen.Api.Catalog;
using Lumen.Api.Checkout;
using Lumen.Api.Identity;
using Lumen.Api.Infrastructure.Data;
using Lumen.Api.Orders;
using Lumen.Api.Partners.Harbor;
using Lumen.Api.Partners.Kestrel;
using Lumen.Api.Payments;
using Lumen.Api.Wallets;
using Lumen.Domain.Catalog;
using Lumen.Domain.Money;
using Lumen.Domain.Pricing;
using Lumen.IntegrationTests.Catalog;
using Lumen.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Lumen.IntegrationTests.Checkout;

/// <summary>
/// The real API on the real database for checkout and payment tests, one per test class: its own fake clock (so a
/// class can move time without disturbing another) and stub partners for the live checks. Background jobs are off;
/// job tests call RunOnceAsync. Each test makes its own customer and products, so tests never share a cart, a wallet,
/// stock or an order.
/// </summary>
public sealed class CheckoutHost(LumenApiFactory factory) : IAsyncLifetime
{
    public const decimal EurToUsd = 1.0850m;

    public static readonly ShippingAddressDto Address = new("Ada Lovelace", "1200 NW Naito Pkwy", null, "Portland", "OR", "97209", "US");

    private readonly ConcurrentBag<Guid> _shoppers = [];

    public FakeTimeProvider Clock { get; } = new(WholeSecond(DateTimeOffset.UtcNow));

    public StubHarbor Harbor { get; } = new();

    public StubKestrel Kestrel { get; } = new();

    /// <summary>The API's gateway: the mock, with a switch to make its status lookup fail for chosen payments.</summary>
    public TestGateway Gateway { get; } = new();

    public WebApplicationFactory<Program> App { get; private set; } = null!;

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        App = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Jobs:Enabled", "false");

            // The job tests' runs scan the whole shared database; one run must reach the rows a test is about.
            builder.UseSetting("PaymentJobs:BatchSize", "1000");

            // Polly takes the fake clock from DI: a retry back-off on it would wait for a clock nobody moves, so the one
            // retry goes at once.
            builder.UseSetting("Partners:Harbor:BaseUrl", "http://harbor.test");
            builder.UseSetting("Partners:Harbor:ApiKey", StubHarbor.Key);
            builder.UseSetting("Partners:Harbor:Resilience:MaxRetries", "1");
            builder.UseSetting("Partners:Harbor:Resilience:RetryBaseDelay", "00:00:00");
            builder.UseSetting("Partners:Kestrel:BaseUrl", "http://kestrel.test");
            builder.UseSetting("Partners:Kestrel:Username", StubKestrel.User);
            builder.UseSetting("Partners:Kestrel:Password", StubKestrel.Password);
            builder.UseSetting("Partners:Kestrel:Resilience:MaxRetries", "1");
            builder.UseSetting("Partners:Kestrel:Resilience:RetryBaseDelay", "00:00:00");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<TimeProvider>(Clock);
                services.AddSingleton<MockPaymentGateway>();
                services.AddSingleton<IPaymentGateway>(sp =>
                {
                    Gateway.Inner = sp.GetRequiredService<MockPaymentGateway>();
                    return Gateway;
                });
                services.AddHttpClient(HarborCatalogSource.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Harbor);
                services.AddHttpClient(KestrelCatalogSource.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Kestrel);
            });
        });
        _ = App.Server;
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await App.Services.GetRequiredService<DetachedPaymentRunner>().WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await RemoveShoppersDataAsync();
        await App.DisposeAsync();
    }

    /// <summary>
    /// Removes the orders, payments, refunds, wallet entries and carts of this class's customers. Their order and cart
    /// lines reference partner products, and the partner sync tests start by deleting every partner product.
    /// </summary>
    private async Task RemoveShoppersDataAsync()
    {
        var ids = _shoppers.ToList();
        await using var scope = App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LumenDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var orders = db.Orders.Where(o => ids.Contains(o.CustomerId)).Select(o => o.Id);
        await db.WalletTransactions.Where(t => ids.Contains(t.CustomerId) && t.Kind != WalletTransactionKind.Seed).ExecuteDeleteAsync();
        await db.Refunds.Where(r => orders.Contains(r.OrderId)).ExecuteDeleteAsync();
        await db.PartnerOrders.Where(p => db.OrderLines.Any(l => l.Id == p.OrderLineId && orders.Contains(l.OrderId))).ExecuteDeleteAsync();
        await db.Payments.Where(p => orders.Contains(p.OrderId)).ExecuteDeleteAsync();
        await db.Orders.Where(o => ids.Contains(o.CustomerId)).ExecuteDeleteAsync();
        await db.Carts.Where(c => c.CustomerId != null && ids.Contains(c.CustomerId.Value)).ExecuteDeleteAsync();
        await transaction.CommitAsync();
    }

    /// <summary>A newly registered, signed-in customer ($500.00 wallet, empty cart).</summary>
    public async Task<Shopper> NewShopperAsync()
    {
        var session = new ApiSession(App.CreateClient());
        await session.StartAsync();
        var email = $"shopper{Guid.NewGuid():N}@example.test";
        (await session.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(email, "Shopper!2026"))).EnsureSuccessStatusCode();
        await session.StartAsync();
        var id = await WithDbAsync(db => db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync(Ct));
        _shoppers.Add(id);
        return new Shopper(this, session, id);
    }

    /// <summary>An own product, listed and sellable.</summary>
    public async Task<Product> OwnProductAsync(decimal price = 18.50m, int onHand = 10)
    {
        var product = CatalogTestSupport.Own($"Checkout test mug {Guid.NewGuid():N}", price, onHand);
        await InsertAsync(product);
        return product;
    }

    /// <summary>A Harbor product as the last sync stored it, and the same item live at Harbor.</summary>
    /// <param name="onHand">LIMITED with this many on hand, or AVAILABLE (no number) when null.</param>
    public async Task<Product> HarborProductAsync(decimal price = 49.90m, int? onHand = null)
    {
        var product = CatalogTestSupport.Partner(ProductSources.Harbor, $"Checkout test tote {Guid.NewGuid():N}", price, onHand);
        await InsertAsync(product);
        Harbor.Set(product.SourceProductId, (int)(price * 100), onHand);
        return product;
    }

    /// <summary>A Kestrel product priced in EUR by the last feed (converted at 1.0850), with live stock.</summary>
    public async Task<Product> KestrelProductAsync(decimal eur = 14.95m, int stock = 20)
    {
        var product = CatalogTestSupport.Partner(ProductSources.Kestrel, $"Checkout test socks {Guid.NewGuid():N}",
            MoneyMath.ConvertToUsd(eur, EurToUsd), stock);
        product.SourcePrice = eur;
        product.SourceCurrency = "EUR";
        product.FxRateToUsd = EurToUsd;
        await InsertAsync(product);
        Kestrel.Stock[product.SourceProductId] = stock;
        return product;
    }

    public Task InsertAsync(params Product[] products) => WithDbAsync(async db =>
    {
        db.Products.AddRange(products);
        await db.SaveChangesAsync(Ct);
        return 0;
    });

    /// <summary>Puts a line in the customer's saved cart, as the cart feature would (seen price = current price).</summary>
    public Task AddToCartAsync(Guid customerId, Product product, int quantity) => WithDbAsync(async db =>
    {
        var now = Clock.GetUtcNow();
        var cart = await db.Carts.SingleOrDefaultAsync(c => c.CustomerId == customerId, Ct);
        if (cart is null)
        {
            cart = new Cart { CustomerId = customerId, CreatedUtc = now, UpdatedUtc = now };
            db.Carts.Add(cart);
        }

        db.CartLines.Add(new CartLine
        {
            CartId = cart.Id,
            ProductId = product.Id,
            Quantity = quantity,
            UnitPriceSeenUsd = product.PriceUsd ?? 0m,
            AvailabilitySeen = AvailabilityStatus.InStock,
            SellableSeen = true,
            AddedUtc = now,
        });
        await db.SaveChangesAsync(Ct);
        return 0;
    });

    public async Task<T> WithDbAsync<T>(Func<LumenDbContext, Task<T>> work)
    {
        await using var scope = App.Services.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<LumenDbContext>());
    }

    public Task<Product> ProductAsync(Guid id) => WithDbAsync(db => db.Products.AsNoTracking().SingleAsync(p => p.Id == id, Ct));

    public Task<Order> OrderAsync(string number) => WithDbAsync(db => db.Orders.AsNoTracking().AsSplitQuery()
        .Include(o => o.Lines).ThenInclude(l => l.PartnerOrder)
        .Include(o => o.Shipments)
        .Include(o => o.Payments)
        .Include(o => o.Refunds)
        .SingleAsync(o => o.Number == number, Ct));

    public Task<List<MockGatewayCharge>> GatewayChargesAsync(Guid orderId) => WithDbAsync(db =>
        db.MockGatewayCharges.AsNoTracking()
            .Where(c => db.Payments.Any(p => p.OrderId == orderId && p.Id == c.IdempotencyKey))
            .ToListAsync(Ct));

    public Task<List<PaymentEvent>> EventsAsync(Guid paymentId) => WithDbAsync(db =>
        db.PaymentEvents.AsNoTracking().Where(e => e.PaymentId == paymentId).OrderBy(e => e.OccurredUtc).ToListAsync(Ct));

    /// <summary>Every card charge started so far has finished (they run detached from the requests).</summary>
    public Task ChargesFinishedAsync() =>
        App.Services.GetRequiredService<DetachedPaymentRunner>().WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30), Ct);

    /// <summary>
    /// Waits (real time, bounded) until the mock gateway has recorded the charge: from then on its slow-card timer is
    /// running on the fake clock, so advancing the clock releases it.
    /// </summary>
    public async Task GatewayHasChargeAsync(Guid paymentId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await WithDbAsync(db => db.MockGatewayCharges.AnyAsync(c => c.IdempotencyKey == paymentId, Ct)))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"The gateway never saw payment {paymentId}.");
            }

            await Task.Delay(20, Ct);
        }
    }

    public static async Task<HttpResponseMessage> SendJsonAsync<T>(ApiSession session, HttpMethod method, string url, T body)
    {
        using var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body, options: CatalogTestSupport.Json) };
        if (session.XsrfToken is { } token)
        {
            request.Headers.Add("X-XSRF-TOKEN", token);
        }

        return await session.Client.SendAsync(request, Ct);
    }

    private static DateTimeOffset WholeSecond(DateTimeOffset value) => new(value.UtcTicks - value.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
}

/// <summary>The mock gateway, except that <see cref="StatusUnavailable"/> payments get no answer to a status lookup.</summary>
public sealed class TestGateway : IPaymentGateway
{
    public IPaymentGateway Inner { get; set; } = null!;

    public ConcurrentBag<Guid> StatusUnavailable { get; } = [];

    public Task<GatewayChargeResult> ChargeAsync(GatewayChargeRequest request, CancellationToken cancellationToken) =>
        Inner.ChargeAsync(request, cancellationToken);

    public Task<GatewayChargeResult> SubmitOneTimeCodeAsync(Guid paymentId, string code, CancellationToken cancellationToken) =>
        Inner.SubmitOneTimeCodeAsync(paymentId, code, cancellationToken);

    public Task<GatewayChargeResult> GetChargeAsync(Guid paymentId, CancellationToken cancellationToken) =>
        StatusUnavailable.Contains(paymentId)
            ? throw new HttpRequestException("Gateway status lookup unavailable (test).")
            : Inner.GetChargeAsync(paymentId, cancellationToken);

    public Task<GatewayRefundResult> RefundAsync(GatewayRefundRequest request, CancellationToken cancellationToken) =>
        Inner.RefundAsync(request, cancellationToken);
}

/// <summary>A signed-in customer driving the checkout and payment API like the storefront does.</summary>
public sealed class Shopper(CheckoutHost host, ApiSession session, Guid id)
{
    public Guid Id => id;

    public ApiSession Session => session;

    public Task AddToCartAsync(Product product, int quantity = 1) => host.AddToCartAsync(id, product, quantity);

    public async Task<CheckoutPreviewDto> PreviewAsync(string shippingMethod = "standard")
    {
        var response = await session.GetAsync($"/api/v1/checkout/preview?shippingMethod={shippingMethod}");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CheckoutHost.Ct));
        return await response.ReadAsync<CheckoutPreviewDto>();
    }

    /// <summary>Place order with exactly what <paramref name="preview"/> showed (the review page's request).</summary>
    public Task<HttpResponseMessage> PlaceAsync(CheckoutPreviewDto preview, Guid? checkoutKey = null) =>
        PlaceAsync(Request(preview, checkoutKey ?? Guid.NewGuid()));

    public Task<HttpResponseMessage> PlaceAsync(PlaceOrderRequest request) =>
        CheckoutHost.SendJsonAsync(session, HttpMethod.Post, "/api/v1/checkout/orders", request);

    public static PlaceOrderRequest Request(CheckoutPreviewDto preview, Guid checkoutKey) => new(
        checkoutKey,
        [.. preview.Shipments.SelectMany(s => s.Lines).Select(l => new PlaceOrderLineDto(l.ProductId, l.Quantity, l.UnitPrice))],
        preview.Total,
        CheckoutHost.Address,
        preview.ShippingMethod);

    /// <summary>Preview, then place exactly that; the order must be created.</summary>
    public async Task<OrderDetailDto> PlaceOrderAsync(string shippingMethod = "standard")
    {
        var response = await PlaceAsync(await PreviewAsync(shippingMethod));
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(CheckoutHost.Ct));
        return await response.ReadAsync<OrderDetailDto>();
    }

    public Task<HttpResponseMessage> PayByCardAsync(string orderNumber, string cardNumber, string cvc = "123") =>
        CheckoutHost.SendJsonAsync(session, HttpMethod.Post, $"/api/v1/orders/{orderNumber}/payments",
            new PayRequest(PaymentMethod.Card, new CardDetailsDto(cardNumber, "Ada Lovelace", 12, host.Clock.GetUtcNow().Year + 2, cvc)));

    public Task<HttpResponseMessage> PayByWalletAsync(string orderNumber) =>
        CheckoutHost.SendJsonAsync(session, HttpMethod.Post, $"/api/v1/orders/{orderNumber}/payments", new PayRequest(PaymentMethod.Wallet, null));

    public Task<HttpResponseMessage> SubmitCodeAsync(string orderNumber, string code) =>
        CheckoutHost.SendJsonAsync(session, HttpMethod.Post, $"/api/v1/orders/{orderNumber}/payments/current/one-time-code", new OneTimeCodeRequest(code));

    public async Task<PaymentStatusDto> PaymentAsync(string orderNumber)
    {
        var response = await session.GetAsync($"/api/v1/orders/{orderNumber}/payments/current");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(CheckoutHost.Ct));
        return await response.ReadAsync<PaymentStatusDto>();
    }

    /// <summary>Pays by card and waits for the background charge to finish; returns the payment as the page then shows it.</summary>
    public async Task<PaymentStatusDto> PayByCardAndWaitAsync(string orderNumber, string cardNumber)
    {
        var response = await PayByCardAsync(orderNumber, cardNumber);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(CheckoutHost.Ct));
        await host.ChargesFinishedAsync();
        return await PaymentAsync(orderNumber);
    }

    public Task<decimal> WalletBalanceAsync() => host.WithDbAsync(db => db.Wallets.Where(w => w.CustomerId == id).Select(w => w.Balance).SingleAsync(CheckoutHost.Ct));

    /// <summary>Sets the balance directly (there is no top-up): the wallet tests need exact amounts.</summary>
    public async Task SetWalletBalanceAsync(decimal balance)
    {
        await using var scope = host.App.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<WalletService>().EnsureOpenAsync(id, CheckoutHost.Ct);
        await scope.ServiceProvider.GetRequiredService<LumenDbContext>().Wallets.Where(w => w.CustomerId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.Balance, balance), CheckoutHost.Ct);
    }
}
