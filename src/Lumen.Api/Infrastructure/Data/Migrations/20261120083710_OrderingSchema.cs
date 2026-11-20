using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumen.Api.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class OrderingSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Carts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: true),
                    AnonymousTokenHash = table.Column<byte[]>(type: "bytea", nullable: true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Carts", x => x.Id);
                    table.CheckConstraint("CK_Carts_GuestOrCustomer", "(\"CustomerId\" IS NULL) <> (\"AnonymousTokenHash\" IS NULL)");
                    table.CheckConstraint("CK_Carts_TokenHashIsSha256", "\"AnonymousTokenHash\" IS NULL OR octet_length(\"AnonymousTokenHash\") = 32");
                    table.ForeignKey(
                        name: "FK_Carts_AspNetUsers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MockGatewayCharges",
                columns: table => new
                {
                    IdempotencyKey = table.Column<Guid>(type: "uuid", nullable: false),
                    CardBrand = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CardLast4 = table.Column<string>(type: "character(4)", fixedLength: true, maxLength: 4, nullable: false),
                    Behaviour = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DeclineReason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    ErrorCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RefundedAmount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MockGatewayCharges", x => x.IdempotencyKey);
                    table.CheckConstraint("CK_MockGatewayCharges_Amount", "\"Amount\" >= 0");
                    table.CheckConstraint("CK_MockGatewayCharges_CardLast4IsFourDigits", "\"CardLast4\" ~ '^[0-9]{4}$'");
                    table.CheckConstraint("CK_MockGatewayCharges_ErrorCount", "\"ErrorCount\" >= 0");
                    table.CheckConstraint("CK_MockGatewayCharges_RefundedWithinAmount", "\"RefundedAmount\" >= 0 AND \"RefundedAmount\" <= \"Amount\"");
                });

            migrationBuilder.CreateTable(
                name: "Orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    CheckoutKey = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Subtotal = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    ShippingTotal = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    TaxTotal = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    Total = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    RefundedTotal = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    ShippingMethodForOwnItems = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    FailedPaymentAttempts = table.Column<int>(type: "integer", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    HoldExpiresUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PaidUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    ShipToCity = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ShipToCountry = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    ShipToLine1 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ShipToLine2 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ShipToName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ShipToPostalCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ShipToRegion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Orders", x => x.Id);
                    table.CheckConstraint("CK_Orders_AmountsNotNegative", "\"Subtotal\" >= 0 AND \"ShippingTotal\" >= 0 AND \"TaxTotal\" >= 0 AND \"RefundedTotal\" >= 0");
                    table.CheckConstraint("CK_Orders_FailedPaymentAttempts", "\"FailedPaymentAttempts\" >= 0");
                    table.CheckConstraint("CK_Orders_RefundedNotAboveTotal", "\"RefundedTotal\" <= \"Total\"");
                    table.CheckConstraint("CK_Orders_TotalIsSum", "\"Total\" = \"Subtotal\" + \"ShippingTotal\" + \"TaxTotal\"");
                    table.ForeignKey(
                        name: "FK_Orders_AspNetUsers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Wallets",
                columns: table => new
                {
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Balance = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Wallets", x => x.CustomerId);
                    table.CheckConstraint("CK_Wallets_BalanceNotNegative", "\"Balance\" >= 0");
                    table.ForeignKey(
                        name: "FK_Wallets_AspNetUsers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CartLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CartId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    UnitPriceSeenUsd = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    AvailabilitySeen = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SellableSeen = table.Column<bool>(type: "boolean", nullable: false),
                    AddedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NoticeAcknowledgedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    QuantityLimitedFrom = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CartLines", x => x.Id);
                    table.CheckConstraint("CK_CartLines_Quantity", "\"Quantity\" BETWEEN 1 AND 10");
                    table.CheckConstraint("CK_CartLines_QuantityLimitedFrom", "\"QuantityLimitedFrom\" IS NULL OR \"QuantityLimitedFrom\" > \"Quantity\"");
                    table.CheckConstraint("CK_CartLines_UnitPriceSeen", "\"UnitPriceSeenUsd\" >= 0");
                    table.ForeignKey(
                        name: "FK_CartLines_Carts_CartId",
                        column: x => x.CartId,
                        principalTable: "Carts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CartLines_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MockGatewayRefunds",
                columns: table => new
                {
                    IdempotencyKey = table.Column<Guid>(type: "uuid", nullable: false),
                    ChargeIdempotencyKey = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MockGatewayRefunds", x => x.IdempotencyKey);
                    table.CheckConstraint("CK_MockGatewayRefunds_AmountPositive", "\"Amount\" > 0");
                    table.ForeignKey(
                        name: "FK_MockGatewayRefunds_MockGatewayCharges_ChargeIdempotencyKey",
                        column: x => x.ChargeIdempotencyKey,
                        principalTable: "MockGatewayCharges",
                        principalColumn: "IdempotencyKey",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OrderLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    LineNumber = table.Column<int>(type: "integer", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SourceProductId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    PrimaryImageSourceUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    UnitPriceUsd = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    LineSubtotal = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    Tax = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    SourceUnitPrice = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    SourceCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    FxRateToUsd = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StatusReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RefundedAmount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderLines", x => x.Id);
                    table.CheckConstraint("CK_OrderLines_AmountsNotNegative", "\"UnitPriceUsd\" >= 0 AND \"Tax\" >= 0 AND \"RefundedAmount\" >= 0");
                    table.CheckConstraint("CK_OrderLines_FxRatePositive", "\"FxRateToUsd\" IS NULL OR \"FxRateToUsd\" > 0");
                    table.CheckConstraint("CK_OrderLines_LineNumber", "\"LineNumber\" >= 1");
                    table.CheckConstraint("CK_OrderLines_Quantity", "\"Quantity\" >= 1");
                    table.CheckConstraint("CK_OrderLines_RefundedNotAboveLineTotal", "\"RefundedAmount\" <= \"LineSubtotal\" + \"Tax\"");
                    table.CheckConstraint("CK_OrderLines_SubtotalIsUnitTimesQuantity", "\"LineSubtotal\" = \"UnitPriceUsd\" * \"Quantity\"");
                    table.ForeignKey(
                        name: "FK_OrderLines_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OrderLines_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OrderShipments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Method = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Fee = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    FeeRefunded = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderShipments", x => x.Id);
                    table.CheckConstraint("CK_OrderShipments_Fee", "\"Fee\" >= 0");
                    table.ForeignKey(
                        name: "FK_OrderShipments_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Payments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Method = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StatusChangedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AutomaticRetryUsed = table.Column<bool>(type: "boolean", nullable: false),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    DeclineReason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    CardBrand = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    CardLast4 = table.Column<string>(type: "character(4)", fixedLength: true, maxLength: 4, nullable: true),
                    OneTimeCodeIssuedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    OneTimeCodeExpiresUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payments", x => x.Id);
                    table.CheckConstraint("CK_Payments_AmountNotNegative", "\"Amount\" >= 0");
                    table.CheckConstraint("CK_Payments_AttemptNumber", "\"AttemptNumber\" >= 1");
                    table.CheckConstraint("CK_Payments_CardDataOnlyForCards", "(\"Method\" = 'Card') = (\"CardLast4\" IS NOT NULL AND \"CardBrand\" IS NOT NULL)");
                    table.CheckConstraint("CK_Payments_CardLast4IsFourDigits", "\"CardLast4\" IS NULL OR \"CardLast4\" ~ '^[0-9]{4}$'");
                    table.ForeignKey(
                        name: "FK_Payments_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PartnerOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ExternalRef = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PartnerOrderId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SubmittedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AcceptedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DecidedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PartnerOrders", x => x.Id);
                    table.CheckConstraint("CK_PartnerOrders_Attempts", "\"Attempts\" >= 0");
                    table.ForeignKey(
                        name: "FK_PartnerOrders_OrderLines_OrderLineId",
                        column: x => x.OrderLineId,
                        principalTable: "OrderLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PaymentEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PaymentId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    Detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentEvents_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PaymentEvents_Payments_PaymentId",
                        column: x => x.PaymentId,
                        principalTable: "Payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Refunds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    PaymentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    LineIds = table.Column<string>(type: "jsonb", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Refunds", x => x.Id);
                    table.CheckConstraint("CK_Refunds_AmountPositive", "\"Amount\" > 0");
                    table.CheckConstraint("CK_Refunds_Attempts", "\"Attempts\" >= 0");
                    table.ForeignKey(
                        name: "FK_Refunds_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Refunds_Payments_PaymentId",
                        column: x => x.PaymentId,
                        principalTable: "Payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WalletTransactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    BalanceAfter = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: true),
                    PaymentId = table.Column<Guid>(type: "uuid", nullable: true),
                    RefundId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalletTransactions", x => x.Id);
                    table.CheckConstraint("CK_WalletTransactions_BalanceAfterNotNegative", "\"BalanceAfter\" >= 0");
                    table.CheckConstraint("CK_WalletTransactions_KindMatchesAmount", "(\"Kind\" = 'Seed' AND \"Amount\" > 0) OR (\"Kind\" = 'Payment' AND \"Amount\" < 0 AND \"PaymentId\" IS NOT NULL AND \"OrderId\" IS NOT NULL) OR (\"Kind\" = 'Refund' AND \"Amount\" > 0 AND \"RefundId\" IS NOT NULL AND \"OrderId\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_WalletTransactions_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WalletTransactions_Payments_PaymentId",
                        column: x => x.PaymentId,
                        principalTable: "Payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WalletTransactions_Refunds_RefundId",
                        column: x => x.RefundId,
                        principalTable: "Refunds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WalletTransactions_Wallets_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Wallets",
                        principalColumn: "CustomerId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CartLines_CartId_ProductId",
                table: "CartLines",
                columns: new[] { "CartId", "ProductId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CartLines_ProductId",
                table: "CartLines",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_Carts_AnonymousTokenHash",
                table: "Carts",
                column: "AnonymousTokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Carts_CustomerId",
                table: "Carts",
                column: "CustomerId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Carts_Guest_UpdatedUtc",
                table: "Carts",
                column: "UpdatedUtc",
                filter: "\"CustomerId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MockGatewayRefunds_ChargeIdempotencyKey",
                table: "MockGatewayRefunds",
                column: "ChargeIdempotencyKey");

            migrationBuilder.CreateIndex(
                name: "IX_OrderLines_OrderId_LineNumber",
                table: "OrderLines",
                columns: new[] { "OrderId", "LineNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderLines_ProductId",
                table: "OrderLines",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderShipments_OrderId_Source",
                table: "OrderShipments",
                columns: new[] { "OrderId", "Source" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Orders_CheckoutKey",
                table: "Orders",
                column: "CheckoutKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Orders_CustomerId_CreatedUtc",
                table: "Orders",
                columns: new[] { "CustomerId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_Number",
                table: "Orders",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Orders_Status_CreatedUtc",
                table: "Orders",
                columns: new[] { "Status", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_Unpaid_HoldExpiresUtc",
                table: "Orders",
                column: "HoldExpiresUtc",
                filter: "\"Status\" IN ('AwaitingPayment', 'PaymentProcessing')");

            migrationBuilder.CreateIndex(
                name: "IX_PartnerOrders_ExternalRef",
                table: "PartnerOrders",
                column: "ExternalRef",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PartnerOrders_OrderLineId",
                table: "PartnerOrders",
                column: "OrderLineId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PartnerOrders_Status_NextAttemptUtc",
                table: "PartnerOrders",
                columns: new[] { "Status", "NextAttemptUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentEvents_OrderId_OccurredUtc",
                table: "PaymentEvents",
                columns: new[] { "OrderId", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentEvents_PaymentId",
                table: "PaymentEvents",
                column: "PaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_OrderId_CreatedUtc",
                table: "Payments",
                columns: new[] { "OrderId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_OrderId_NotFinal",
                table: "Payments",
                column: "OrderId",
                unique: true,
                filter: "\"Status\" IN ('Created', 'Processing', 'RequiresAction', 'Error', 'Failed')");

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_OrderId",
                table: "Refunds",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_PaymentId",
                table: "Refunds",
                column: "PaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_Status_NextAttemptUtc",
                table: "Refunds",
                columns: new[] { "Status", "NextAttemptUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WalletTransactions_CustomerId_CreatedUtc",
                table: "WalletTransactions",
                columns: new[] { "CustomerId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WalletTransactions_CustomerId_Seed",
                table: "WalletTransactions",
                column: "CustomerId",
                unique: true,
                filter: "\"Kind\" = 'Seed'");

            migrationBuilder.CreateIndex(
                name: "IX_WalletTransactions_OrderId",
                table: "WalletTransactions",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_WalletTransactions_PaymentId_Payment",
                table: "WalletTransactions",
                column: "PaymentId",
                unique: true,
                filter: "\"Kind\" = 'Payment'");

            migrationBuilder.CreateIndex(
                name: "IX_WalletTransactions_RefundId_Refund",
                table: "WalletTransactions",
                column: "RefundId",
                unique: true,
                filter: "\"Kind\" = 'Refund'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CartLines");

            migrationBuilder.DropTable(
                name: "MockGatewayRefunds");

            migrationBuilder.DropTable(
                name: "OrderShipments");

            migrationBuilder.DropTable(
                name: "PartnerOrders");

            migrationBuilder.DropTable(
                name: "PaymentEvents");

            migrationBuilder.DropTable(
                name: "WalletTransactions");

            migrationBuilder.DropTable(
                name: "Carts");

            migrationBuilder.DropTable(
                name: "MockGatewayCharges");

            migrationBuilder.DropTable(
                name: "OrderLines");

            migrationBuilder.DropTable(
                name: "Refunds");

            migrationBuilder.DropTable(
                name: "Wallets");

            migrationBuilder.DropTable(
                name: "Payments");

            migrationBuilder.DropTable(
                name: "Orders");
        }
    }
}
