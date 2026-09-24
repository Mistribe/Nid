using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FxRateQuoteIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_fx_rate_quote_on_date",
                schema: "core",
                table: "fx_rate",
                columns: new[] { "quote", "on_date" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_fx_rate_quote_on_date",
                schema: "core",
                table: "fx_rate");
        }
    }
}
