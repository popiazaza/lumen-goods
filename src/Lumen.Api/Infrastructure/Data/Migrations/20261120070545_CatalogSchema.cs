using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumen.Api.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class CatalogSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "partner_sync_errors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Partner = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OccurredUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Operation = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StatusCode = table.Column<int>(type: "integer", nullable: true),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_partner_sync_errors", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "partner_sync_state",
                columns: table => new
                {
                    Partner = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LastAttemptUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSuccessUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    LiveProductCount = table.Column<int>(type: "integer", nullable: false),
                    Watermark = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    LastFullSyncUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StockRefreshedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RefreshRequestedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_partner_sync_state", x => x.Partner);
                });

            migrationBuilder.CreateTable(
                name: "products",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SourceProductId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    Category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PriceUsd = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    SourcePrice = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    SourceCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    FxRateToUsd = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    Quantity = table.Column<int>(type: "integer", nullable: true),
                    StatusWhenQuantityUnknown = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OnHand = table.Column<int>(type: "integer", nullable: true),
                    Reserved = table.Column<int>(type: "integer", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsListed = table.Column<bool>(type: "boolean", nullable: false),
                    Images = table.Column<string>(type: "jsonb", nullable: false),
                    Attributes = table.Column<string>(type: "jsonb", nullable: false),
                    FirstSeenUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastUpdatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_products", x => x.Id);
                    table.CheckConstraint("ck_products_own_stock", "\"OnHand\" IS NULL OR (\"OnHand\" >= 0 AND \"Reserved\" >= 0 AND \"Reserved\" <= \"OnHand\")");
                });

            migrationBuilder.CreateIndex(
                name: "IX_partner_sync_errors_Partner_OccurredUtc",
                table: "partner_sync_errors",
                columns: new[] { "Partner", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_products_FirstSeenUtc",
                table: "products",
                column: "FirstSeenUtc");

            migrationBuilder.CreateIndex(
                name: "IX_products_IsListed_Category",
                table: "products",
                columns: new[] { "IsListed", "Category" });

            migrationBuilder.CreateIndex(
                name: "IX_products_PriceUsd",
                table: "products",
                column: "PriceUsd");

            migrationBuilder.CreateIndex(
                name: "IX_products_Source_SourceProductId",
                table: "products",
                columns: new[] { "Source", "SourceProductId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "partner_sync_errors");

            migrationBuilder.DropTable(
                name: "partner_sync_state");

            migrationBuilder.DropTable(
                name: "products");
        }
    }
}
