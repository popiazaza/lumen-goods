using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lumen.Api.Infrastructure.Data.Migrations
{
    /// <summary>
    /// BR-CAT-03: search reads <c>Products.DescriptionText</c>, a stored generated column. Adding it rewrites
    /// <c>Products</c> under an exclusive lock, and the index is built without CONCURRENTLY: instant at launch size,
    /// a maintenance-window step at catalogue scale (DESIGN §10).
    /// </summary>
    public partial class SearchDescriptionText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Products_Description_Trgm",
                table: "Products");

            migrationBuilder.AddColumn<string>(
                name: "DescriptionText",
                table: "Products",
                type: "text",
                nullable: false,
                computedColumnSql: "CASE WHEN \"Source\" = 'lumen' THEN \"Description\"\nELSE btrim(regexp_replace(\n    replace(replace(replace(replace(regexp_replace(\"Description\", '<[^>]*>', ' ', 'g'),\n        '&lt;', '<'), '&gt;', '>'), '&nbsp;', ' '), '&amp;', '&'),\n    '\\s+', ' ', 'g'))\nEND",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "IX_Products_DescriptionText_Trgm",
                table: "Products",
                column: "DescriptionText")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Products_DescriptionText_Trgm",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "DescriptionText",
                table: "Products");

            migrationBuilder.CreateIndex(
                name: "IX_Products_Description_Trgm",
                table: "Products",
                column: "Description")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });
        }
    }
}
