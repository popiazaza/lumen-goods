using System.Globalization;
using Lumen.Api.Catalog;
using Lumen.Api.Orders;
using Lumen.Domain.Catalog;
using Lumen.Domain.Orders;
using Lumen.Domain.Payments;
using Microsoft.Extensions.Options;
using static Lumen.UnitTests.Orders.LineRejectionTests;

namespace Lumen.UnitTests.Orders;

/// <summary>BR-ORD-02: what the customer reads for each line and the order, with the seller's name.</summary>
public sealed class OrderPresentationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 8, 30, 0, TimeSpan.Zero);

    private static readonly SellerDirectory Sellers = new(new FixedMonitor<CatalogOptions>(new CatalogOptions
    {
        Sellers =
        [
            new SellerOptions { Source = ProductSources.Lumen, Name = "Lumen Goods" },
            new SellerOptions { Source = ProductSources.Harbor, Name = "Harbor Supply Co." },
            new SellerOptions { Source = ProductSources.Kestrel, Name = "Kestrel Outfitters" },
        ],
    }));

    [Theory]
    [InlineData(OrderLineStatus.AwaitingPartner, ProductSources.Kestrel, "Kestrel Outfitters", "0", false, "Awaiting Kestrel Outfitters")]
    [InlineData(OrderLineStatus.Confirmed, ProductSources.Harbor, "Harbor Supply Co.", "0", false, "Confirmed by Harbor Supply Co.")]
    [InlineData(OrderLineStatus.Confirmed, ProductSources.Lumen, "Lumen Goods", "0", false, "Confirmed")]
    [InlineData(OrderLineStatus.NeedsAttention, ProductSources.Kestrel, "Kestrel Outfitters", "0", false, "Delayed")]
    [InlineData(OrderLineStatus.Rejected, ProductSources.Kestrel, "Kestrel Outfitters", "40.03", false, "Not supplied, refund pending")]
    [InlineData(OrderLineStatus.Rejected, ProductSources.Kestrel, "Kestrel Outfitters", "40.03", true, "Not supplied, refunded $40.03")]
    [InlineData(OrderLineStatus.Rejected, ProductSources.Kestrel, "Kestrel Outfitters", "1355.17", true, "Not supplied, refunded $1,355.17")]
    [InlineData(OrderLineStatus.Rejected, ProductSources.Harbor, "Harbor Supply Co.", "0", false, "Not supplied")]
    [Trait("BR", "BR-ORD-02")]
    public void BR_ORD_02_each_line_says_its_status_with_the_shops_name(
        OrderLineStatus status, string source, string seller, string refunded, bool refundPaid, string expected) =>
        OrderStatusText.ForLine(status, OrderStatus.Paid, source, seller, decimal.Parse(refunded, CultureInfo.InvariantCulture), refundPaid).Should().Be(expected);

    [Theory]
    [InlineData(OrderStatus.AwaitingPayment, "Awaiting payment")]
    [InlineData(OrderStatus.PaymentProcessing, "Confirming your payment")]
    [InlineData(OrderStatus.Paid, "Processing")]
    [InlineData(OrderStatus.Completed, "Confirmed")]
    [InlineData(OrderStatus.PartiallyRefunded, "Partially refunded")]
    [InlineData(OrderStatus.Refunded, "Refunded")]
    [InlineData(OrderStatus.Expired, "Expired")]
    [Trait("BR", "BR-ORD-02")]
    public void BR_ORD_02_the_order_status_in_customer_words(OrderStatus status, string expected) =>
        OrderStatusText.ForOrder(status).Should().Be(expected);

    [Theory]
    [InlineData(OrderStatus.PaymentProcessing, "Waiting for your one-time code")]
    [InlineData(OrderStatus.AwaitingPayment, "Awaiting payment")]
    [InlineData(OrderStatus.Paid, "Processing")]
    [Trait("BR", "BR-ORD-02")]
    public void BR_ORD_02_a_payment_waiting_for_its_one_time_code_says_so_instead_of_confirming(OrderStatus status, string expected) =>
        OrderStatusText.ForOrder(status, awaitingOneTimeCode: true).Should().Be(expected);

    [Theory]
    [InlineData(OrderStatus.AwaitingPayment, "Confirming your payment")]
    [InlineData(OrderStatus.Expired, "Expired")]
    [Trait("BR", "BR-ORD-02")]
    [Trait("BR", "BR-PAY-05")]
    public void BR_PAY_05_an_order_whose_failed_payment_the_gateway_has_yet_to_settle_is_being_confirmed(OrderStatus status, string expected) =>
        OrderStatusText.ForOrder(status, paymentUnsettled: true).Should().Be(expected);

    [Theory]
    [InlineData(OrderStatus.PartiallyRefunded, "Partial refund pending")]
    [InlineData(OrderStatus.Refunded, "Refund pending")]
    [InlineData(OrderStatus.Paid, "Processing")]
    [Trait("BR", "BR-ORD-02")]
    public void BR_ORD_02_a_refunded_order_says_refund_pending_until_the_money_is_back(OrderStatus status, string expected) =>
        OrderStatusText.ForOrder(status, refundPending: true).Should().Be(expected);

    [Fact]
    [Trait("BR", "BR-ORD-02")]
    public void BR_ORD_02_the_detail_groups_lines_by_seller_lumen_first_and_shows_the_refund_with_its_shipping()
    {
        var kestrel = Line(1, ProductSources.Kestrel, 16.22m, 2, OrderLineStatus.AwaitingPartner);
        var own = Line(2, ProductSources.Lumen, 18.50m, 1, OrderLineStatus.Confirmed);
        var harbor = Line(3, ProductSources.Harbor, 49.90m, 1, OrderLineStatus.Confirmed);
        var order = PaidOrder(kestrel, own, harbor);
        order.CreatedUtc = Now.AddMinutes(-30);
        order.HoldExpiresUtc = Now.AddMinutes(-15);
        order.PaidUtc = Now.AddMinutes(-29);

        var processing = OrderDetailMapping.ToDetail(order, new Dictionary<Guid, List<ProductImage>>(), Sellers, Policy(), Now, "https://shop.test");
        processing.Shipments.Select(s => s.SellerName).Should().Equal("Lumen Goods", "Harbor Supply Co.", "Kestrel Outfitters");
        processing.Shipments.SelectMany(s => s.Lines).Select(l => l.StatusText)
            .Should().Equal("Confirmed", "Confirmed by Harbor Supply Co.", "Awaiting Kestrel Outfitters");
        processing.StatusText.Should().Be("Processing");
        processing.IsUpdating.Should().BeTrue();
        processing.CanPay.Should().BeFalse();

        var refund = LineRejection.Reject(order, kestrel, Guid.NewGuid(), "Not supplied by Kestrel Outfitters (OUT_OF_STOCK:K-7731)", Now)!;
        order.Refunds.Add(refund);
        var detail = OrderDetailMapping.ToDetail(order, new Dictionary<Guid, List<ProductImage>>(), Sellers, Policy(), Now, "https://shop.test");

        var line = detail.Shipments.Single(s => s.Source == ProductSources.Kestrel).Lines.Single();
        line.StatusText.Should().Be("Not supplied, refund pending");
        line.RefundedAmount.Should().Be(35.04m);
        detail.Shipments.Single(s => s.Source == ProductSources.Kestrel).FeeRefunded.Should().BeTrue();
        detail.StatusText.Should().Be("Partial refund pending");
        detail.IsUpdating.Should().BeTrue("the page refreshes until the refund is paid back");

        refund.Status = RefundStatus.Succeeded;
        detail = OrderDetailMapping.ToDetail(order, new Dictionary<Guid, List<ProductImage>>(), Sellers, Policy(), Now, "https://shop.test");
        detail.Shipments.Single(s => s.Source == ProductSources.Kestrel).Lines.Single().StatusText.Should().Be("Not supplied, refunded $40.03");
        detail.StatusText.Should().Be("Partially refunded");
        detail.IsUpdating.Should().BeFalse();
        detail.Totals.Refunded.Should().Be(40.03m);
        detail.Totals.NetPaid.Should().Be(order.Total - 40.03m);
        detail.Refunds.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Amount = 40.03m, ShippingAmount = 4.99m, LineIds = new[] { kestrel.Id } });
        detail.Refunds.Single().Reason.Should().Be("Not supplied by Kestrel Outfitters", "the partner's own code is for the admin");

        var admin = OrderDetailMapping.ToDetail(order, new Dictionary<Guid, List<ProductImage>>(), Sellers, Policy(), Now, "https://shop.test", forAdmin: true);
        admin.Refunds.Single().Reason.Should().Be("Not supplied by Kestrel Outfitters (OUT_OF_STOCK:K-7731)");

        refund.Status = RefundStatus.Failed;
        OrderDetailMapping.ToDetail(order, new Dictionary<Guid, List<ProductImage>>(), Sellers, Policy(), Now, "https://shop.test")
            .IsUpdating.Should().BeTrue("a failed refund is retried, so the page keeps refreshing until it is paid back");
    }

    [Fact]
    public void The_line_image_is_a_lumen_media_url_for_the_image_it_was_ordered_with_never_the_partners()
    {
        var harbor = Line(1, ProductSources.Harbor, 49.90m, 1, OrderLineStatus.Confirmed);
        harbor.PrimaryImageSourceUrl = "http://harbor:8080/media/HSC-1/side.jpg";
        var order = PaidOrder(harbor);
        var images = new Dictionary<Guid, List<ProductImage>>
        {
            [harbor.ProductId] = [new ProductImage("http://harbor:8080/media/HSC-1/front.jpg", true), new ProductImage("http://harbor:8080/media/HSC-1/side.jpg", false)],
        };

        var url = OrderDetailMapping.ToDetail(order, images, Sellers, Policy(), Now, "https://shop.test").Shipments.Single().Lines.Single().ImageUrl;

        url.Should().StartWith($"https://shop.test/media/{harbor.ProductId}/1?v=").And.NotContain("harbor:8080");
    }

    private static PaymentWindowPolicy Policy() => new(new PaymentOptions());

    private sealed class FixedMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
