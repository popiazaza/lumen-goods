using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumen.Api.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class PartnerOrderOutcomeUnknown : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "PartnerOrderId",
                table: "PartnerOrders",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "OutcomeUnknown",
                table: "PartnerOrders",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OutcomeUnknown",
                table: "PartnerOrders");

            migrationBuilder.AlterColumn<string>(
                name: "PartnerOrderId",
                table: "PartnerOrders",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);
        }
    }
}
