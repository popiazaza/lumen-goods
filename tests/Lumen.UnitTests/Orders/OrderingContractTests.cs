using System.Text.Json;
using Lumen.Api.Carts;
using Lumen.Api.Catalog;
using Lumen.Api.Checkout;
using Lumen.Api.Infrastructure.Json;
using Lumen.Api.Orders;
using Lumen.Api.Payments;
using Lumen.Api.Wallets;
using Lumen.Domain.Catalog;
using Lumen.Domain.Orders;
using Lumen.Domain.Payments;
using Lumen.Domain.Pricing;

namespace Lumen.UnitTests.Orders;

/// <summary>
/// The ordering contracts as clients receive them, serialised with the API's own settings (<see cref="JsonDefaults"/>):
/// money with exactly two decimals, nulls written rather than left out, enums as snake_case strings.
/// </summary>
public sealed class OrderingContractTests
{
    private static readonly JsonSerializerOptions WebJson = JsonDefaults.Create();
    private static readonly DateTime At = new(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc);

    private static JsonElement Serialize<T>(T value) => JsonSerializer.SerializeToElement(value, WebJson);

    private static CartDto Cart() => new(
        [
            new CartLineDto(
                Guid.Parse("0193a1b2-0000-7000-8000-000000000001"),
                new CartProductDto(Guid.NewGuid(), ProductSources.Harbor, "Harbor Supply Co.", "Canvas Tote", null,
                    new AvailabilityDto("low_stock", 2), true),
                Quantity: 4,
                MaxQuantity: 2,
                UnitPrice: 54.9m,
                UnitPriceSeen: 49.9m,
                LineTotal: 219.6m,
                new CartLineNoticesDto(
                    new PriceChangedNoticeDto(49.9m, 54.9m),
                    new AvailabilityChangedNoticeDto(AvailabilityStatus.InStock, AvailabilityStatus.LowStock),
                    Unavailable: null,
                    new QuantityLimitedNoticeDto(4, 2)),
                BlocksCheckout: true,
                At),
        ],
        ItemCount: 4,
        new CartTotalsDto(219.6m, 4.99m, 17.568m, 242.158m),
        CanCheckout: false);

    [Fact]
    public void Cart_money_has_exactly_two_decimals()
    {
        var json = Serialize(Cart());
        var line = json.GetProperty("lines")[0];

        line.GetProperty("unitPrice").GetRawText().Should().Be("54.90");
        line.GetProperty("unitPriceSeen").GetRawText().Should().Be("49.90");
        line.GetProperty("lineTotal").GetRawText().Should().Be("219.60");
        line.GetProperty("notices").GetProperty("priceChanged").GetProperty("from").GetRawText().Should().Be("49.90");
        json.GetProperty("totals").GetProperty("tax").GetRawText().Should().Be("17.57");
        json.GetProperty("totals").GetProperty("total").GetRawText().Should().Be("242.16");
    }

    [Fact]
    public void Cart_notices_write_nulls_and_enum_strings()
    {
        var notices = Serialize(Cart()).GetProperty("lines")[0].GetProperty("notices");

        notices.GetProperty("unavailable").ValueKind.Should().Be(JsonValueKind.Null);
        notices.GetProperty("availabilityChanged").GetProperty("from").GetString().Should().Be("in_stock");
        notices.GetProperty("availabilityChanged").GetProperty("to").GetString().Should().Be("low_stock");
        notices.GetProperty("quantityLimited").GetProperty("allowed").GetInt32().Should().Be(2);

        var unavailable = Serialize(CartLineNoticesDto.None with { Unavailable = CartLineUnavailableReason.NoLongerAvailable });
        unavailable.GetProperty("unavailable").GetString().Should().Be("no_longer_available");
        unavailable.GetProperty("priceChanged").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void Cart_uses_the_documented_field_names()
    {
        var json = Serialize(Cart());

        json.EnumerateObject().Select(p => p.Name).Should().Equal("lines", "itemCount", "totals", "canCheckout", "maxLines");
        json.GetProperty("lines")[0].EnumerateObject().Select(p => p.Name).Should().Equal(
            "id", "product", "quantity", "maxQuantity", "unitPrice", "unitPriceSeen", "lineTotal", "notices", "blocksCheckout", "addedUtc");
        json.GetProperty("lines")[0].GetProperty("addedUtc").GetString().Should().Be("2026-11-20T08:00:00Z");
    }

    [Fact]
    [Trait("BR", "BR-CHK-01")]
    public void BR_CHK_01_the_price_changed_problem_is_an_rfc_9457_problem_listing_each_change()
    {
        var problem = new PriceChangedProblem(
            "A price changed since you reviewed your order.",
            [
                new CheckoutLineChangeDto(Guid.NewGuid(), Guid.NewGuid(), "Merino Trail Socks", "Kestrel Outfitters",
                    CheckoutChangeKind.PriceChanged, 16.22m, 17.31m, null, null, null, "Price changed from $16.22 to $17.31"),
            ],
            NewTotal: 82.56m);

        var json = Serialize(problem);

        json.EnumerateObject().Select(p => p.Name).Take(3).Should().Equal("type", "title", "status");
        json.GetProperty("type").GetString().Should().Be("urn:lumen:problem:checkout-changed");
        json.GetProperty("status").GetInt32().Should().Be(409);
        json.GetProperty("newTotal").GetRawText().Should().Be("82.56");
        json.GetProperty("canConfirm").GetBoolean().Should().BeTrue();
        var change = json.GetProperty("changes")[0];
        change.GetProperty("kind").GetString().Should().Be("price_changed");
        change.GetProperty("from").GetRawText().Should().Be("16.22");
        change.GetProperty("available").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    [Trait("BR", "BR-CHK-02")]
    public void BR_CHK_02_a_line_that_cannot_be_supplied_cannot_just_be_confirmed()
    {
        var problem = new PriceChangedProblem(
            "Some items cannot be supplied.",
            [
                new CheckoutLineChangeDto(Guid.NewGuid(), Guid.NewGuid(), "Canvas Tote", "Harbor Supply Co.",
                    CheckoutChangeKind.TemporarilyUnavailable, null, null, null, null, null, "Temporarily unavailable"),
            ],
            NewTotal: null);

        var json = Serialize(problem);

        json.GetProperty("newTotal").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("canConfirm").GetBoolean().Should().BeFalse();
        json.GetProperty("changes")[0].GetProperty("kind").GetString().Should().Be("temporarily_unavailable");
    }

    [Fact]
    [Trait("BR", "BR-CHK-01")]
    public void BR_CHK_01_a_cart_that_changed_since_the_review_must_be_reviewed_again()
    {
        var problem = new PriceChangedProblem(
            "Your cart changed since you reviewed your order.",
            [
                new CheckoutLineChangeDto(Guid.NewGuid(), Guid.NewGuid(), "Enamel Camp Mug", "Lumen Goods",
                    CheckoutChangeKind.QuantityChanged, null, null, 2, 3, null, "Quantity changed from 2 to 3"),
                new CheckoutLineChangeDto(null, Guid.NewGuid(), "Canvas Tote", "Harbor Supply Co.",
                    CheckoutChangeKind.CartChanged, null, null, 1, 0, null, "Removed from your cart since you reviewed it"),
            ],
            NewTotal: 61.03m);

        var json = Serialize(problem);

        json.GetProperty("canConfirm").GetBoolean().Should().BeFalse();
        var changes = json.GetProperty("changes");
        changes[0].GetProperty("kind").GetString().Should().Be("quantity_changed");
        changes[0].GetProperty("quantityFrom").GetInt32().Should().Be(2);
        changes[0].GetProperty("quantityTo").GetInt32().Should().Be(3);
        changes[1].GetProperty("kind").GetString().Should().Be("cart_changed");
        changes[1].GetProperty("cartLineId").ValueKind.Should().Be(JsonValueKind.Null);
        changes[1].GetProperty("quantityTo").GetInt32().Should().Be(0);
    }

    [Fact]
    public void Checkout_preview_lines_carry_what_place_order_sends_back()
    {
        var line = new CheckoutLineDto(Guid.NewGuid(), Guid.NewGuid(), "Enamel Camp Mug", null, 18.5m, 2, 37m, 2.96m);

        var json = Serialize(line);

        json.EnumerateObject().Select(p => p.Name).Should().Equal(
            "cartLineId", "productId", "name", "imageUrl", "unitPrice", "quantity", "lineTotal", "tax");
        json.GetProperty("unitPrice").GetRawText().Should().Be("18.50");
        json.GetProperty("lineTotal").GetRawText().Should().Be("37.00");
    }

    [Fact]
    public void Place_order_reads_enums_and_money_from_their_wire_form()
    {
        const string body = """
            { "checkoutKey": "8d7c9e1a-4f7b-4b8e-9a51-3c2d1e0f9a7b",
              "lines": [ { "productId": "0193a0f1-0000-7000-8000-000000000001", "quantity": 2, "unitPrice": 18.50 } ],
              "expectedTotal": 75.46, "shippingMethod": "express",
              "shippingAddress": { "name": "Ada Lovelace", "line1": "1200 NW Naito Pkwy", "line2": null, "city": "Portland",
                                   "region": "or", "postalCode": "97209", "country": "us" } }
            """;

        var request = JsonSerializer.Deserialize<PlaceOrderRequest>(body, WebJson)!;

        request.ShippingMethod.Should().Be(ShippingMethod.Express);
        request.ExpectedTotal.Should().Be(75.46m);
        request.Lines.Should().Equal(new PlaceOrderLineDto(Guid.Parse("0193a0f1-0000-7000-8000-000000000001"), 2, 18.50m));
        request.ShippingAddress!.ToEntity().Should().Be(new ShippingAddress("Ada Lovelace", "1200 NW Naito Pkwy", null, "Portland", "OR", "97209", "US"));
    }

    [Fact]
    public void Enums_are_never_read_from_numbers()
    {
        var read = () => JsonSerializer.Deserialize<PayRequest>("""{ "method": 1, "card": null }""", WebJson);

        read.Should().Throw<JsonException>();
        JsonSerializer.Deserialize<PayRequest>("""{ "method": "wallet", "card": null }""", WebJson)!.Method.Should().Be(PaymentMethod.Wallet);
    }

    [Fact]
    [Trait("BR", "BR-PAY-02")]
    public void BR_PAY_02_a_logged_card_shows_only_its_last_four_digits_and_never_the_cvc()
    {
        var request = new PayRequest(PaymentMethod.Card, new CardDetailsDto("4242 0000 0000 0034", "Ada Lovelace", 12, 2028, "987"));

        var text = request.ToString();

        text.Should().Contain("****0034").And.NotContain("4242").And.NotContain("987").And.NotContain("Ada");
    }

    [Fact]
    public void Payment_status_writes_snake_case_statuses_and_the_code_expiry()
    {
        var status = new PaymentStatusDto(
            Guid.NewGuid(), "LG-7K3M9QX2", PaymentMethod.Card, PaymentStatus.RequiresAction, 75.4m, CardBrand.Visa, "0034",
            NeedsOneTimeCode: true, OneTimeCodeExpiresUtc: At.AddMinutes(5), TestOneTimeCode: "123456", DeclineMessage: null,
            AttemptsLeft: 5, CanRetry: false, PaymentRetryMode.None, OrderStatus.PaymentProcessing, HoldExpiresUtc: At.AddMinutes(15),
            ServerTimeUtc: At.AddSeconds(12));

        var json = Serialize(status);

        json.GetProperty("method").GetString().Should().Be("card");
        json.GetProperty("status").GetString().Should().Be("requires_action");
        json.GetProperty("cardBrand").GetString().Should().Be("visa");
        json.GetProperty("orderStatus").GetString().Should().Be("payment_processing");
        json.GetProperty("amount").GetRawText().Should().Be("75.40");
        json.GetProperty("oneTimeCodeExpiresUtc").GetString().Should().Be("2026-11-20T08:05:00Z");
        json.GetProperty("declineMessage").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("retryMode").GetString().Should().Be("none");
        json.GetProperty("serverTimeUtc").GetString().Should().Be("2026-11-20T08:00:12Z");
    }

    [Fact]
    [Trait("BR", "BR-PAY-05")]
    public void BR_PAY_05_retry_modes_and_the_unresolved_gateway_error_use_snake_case()
    {
        Serialize(PaymentRetryMode.SamePaymentOnly).GetString().Should().Be("same_payment_only");
        Serialize(PaymentRetryMode.AnyMethod).GetString().Should().Be("any_method");
        Serialize(DeclineReason.GatewayErrorUnresolvedNotCharged).GetString().Should().Be("gateway_error_unresolved_not_charged");
    }

    [Fact]
    [Trait("BR", "BR-PAY-01")]
    public void BR_PAY_01_payment_in_progress_is_an_rfc_9457_problem_carrying_the_running_payment()
    {
        var running = new PaymentStatusDto(
            Guid.NewGuid(), "LG-7K3M9QX2", PaymentMethod.Card, PaymentStatus.Processing, 75.46m, CardBrand.Visa, "0042",
            false, null, null, null, 5, false, PaymentRetryMode.None, OrderStatus.PaymentProcessing, At.AddMinutes(15), At);

        var json = Serialize(new PaymentInProgressProblem("Your card payment is still being confirmed.", running));

        json.EnumerateObject().Select(p => p.Name).Take(3).Should().Equal("type", "title", "status");
        json.GetProperty("type").GetString().Should().Be(PaymentProblemTypes.PaymentInProgress);
        json.GetProperty("status").GetInt32().Should().Be(409);
        json.GetProperty("payment").GetProperty("status").GetString().Should().Be("processing");
    }

    [Fact]
    [Trait("BR", "BR-ORD-04")]
    public void BR_ORD_04_an_admin_resolves_a_line_with_confirm_or_reject()
    {
        JsonSerializer.Deserialize<ResolveOrderLineRequest>("{ \"decision\": \"reject\", \"note\": null }", WebJson)!
            .Decision.Should().Be(LineResolution.Reject);
        JsonSerializer.Deserialize<ResolveOrderLineRequest>("{ \"decision\": \"confirm\", \"note\": null }", WebJson)!
            .Decision.Should().Be(LineResolution.Confirm);
    }

    [Fact]
    public void Order_detail_groups_lines_by_seller_with_their_status_and_refund()
    {
        var line = new OrderLineDto(Guid.NewGuid(), 2, Guid.NewGuid(), "Merino Trail Socks", null, 16.22m, 2, 32.44m, 2.6m,
            OrderLineStatus.Rejected, "Not supplied, refunded $35.04", 35.04m);
        var detail = new OrderDetailDto(
            "LG-7K3M9QX2", OrderStatus.PartiallyRefunded, "Partially refunded", At, At.AddMinutes(1), At.AddMinutes(15),
            [new OrderShipmentDto(ProductSources.Kestrel, "Kestrel Outfitters", ShippingMethod.Standard, 4.99m, true, [line])],
            new OrderTotalsDto(69.44m, 17.98m, 5.56m, 92.98m, 40.03m, 52.95m),
            new ShippingAddressDto("Ada Lovelace", "1200 NW Naito Pkwy", null, "Portland", "OR", "97209", "US"),
            ShippingMethod.Express,
            new PaymentSummaryDto(Guid.NewGuid(), PaymentMethod.Wallet, PaymentStatus.Succeeded, 92.98m, null, null, At.AddMinutes(1)),
            [new RefundDto(Guid.NewGuid(), 40.03m, RefundStatus.Succeeded, "Not supplied by Kestrel Outfitters", [line.Id], 4.99m, At, At)],
            CanPay: false,
            IsUpdating: false,
            AwaitingOneTimeCode: false);

        var json = Serialize(detail);

        json.GetProperty("status").GetString().Should().Be("partially_refunded");
        json.GetProperty("shippingMethod").GetString().Should().Be("express");
        var shipment = json.GetProperty("shipments")[0];
        shipment.GetProperty("feeRefunded").GetBoolean().Should().BeTrue();
        shipment.GetProperty("lines")[0].GetProperty("status").GetString().Should().Be("rejected");
        shipment.GetProperty("lines")[0].GetProperty("tax").GetRawText().Should().Be("2.60");
        json.GetProperty("totals").GetProperty("refunded").GetRawText().Should().Be("40.03");
        json.GetProperty("payment").GetProperty("cardLast4").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("refunds")[0].GetProperty("status").GetString().Should().Be("succeeded");
        json.GetProperty("refunds")[0].GetProperty("shippingAmount").GetRawText().Should().Be("4.99");
        json.GetProperty("shippingAddress").GetProperty("line2").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void Admin_partner_orders_and_payment_events_use_snake_case()
    {
        var partnerOrder = new PartnerOrderDto(Guid.NewGuid(), Guid.NewGuid(), 2, ProductSources.Kestrel, "Kestrel Outfitters",
            "LG-7K3M9QX2-2", "KT-5521", PartnerOrderStatus.ClosedByAdmin, null, 4, null, null, At, At, At.AddDays(1), false);
        var paymentEvent = new PaymentEventDto(At, Guid.NewGuid(), PaymentEventType.GatewayError, null, "Gateway error");

        Serialize(partnerOrder).GetProperty("status").GetString().Should().Be("closed_by_admin");
        Serialize(paymentEvent).GetProperty("type").GetString().Should().Be("gateway_error");
        Serialize(paymentEvent).GetProperty("amount").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void Wallet_amounts_are_signed_with_two_decimals()
    {
        var wallet = new WalletDto(1924.5m, "USD",
            [new WalletTransactionDto(Guid.NewGuid(), At, WalletTransactionKind.Payment, -75.46m, 1924.5m, "LG-7K3M9QX2")]);

        var json = Serialize(wallet);

        json.GetProperty("balance").GetRawText().Should().Be("1924.50");
        var transaction = json.GetProperty("recentTransactions")[0];
        transaction.GetProperty("kind").GetString().Should().Be("payment");
        transaction.GetProperty("amount").GetRawText().Should().Be("-75.46");
    }
}
