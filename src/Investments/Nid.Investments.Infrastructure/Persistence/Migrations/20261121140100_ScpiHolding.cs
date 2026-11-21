using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Investments.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScpiHolding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_holding_kind",
                schema: "investments",
                table: "holding");

            migrationBuilder.AddColumn<int>(
                name: "entitlement_delay_months",
                schema: "investments",
                table: "holding",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_holding_entitlement_delay",
                schema: "investments",
                table: "holding",
                sql: "entitlement_delay_months is null or entitlement_delay_months between 0 and 12");

            migrationBuilder.AddCheckConstraint(
                name: "ck_holding_kind",
                schema: "investments",
                table: "holding",
                sql: "kind in ('euro_fund','fund','etf','equity','bond','structured','cash','scpi','other')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_holding_entitlement_delay",
                schema: "investments",
                table: "holding");

            migrationBuilder.DropCheckConstraint(
                name: "ck_holding_kind",
                schema: "investments",
                table: "holding");

            migrationBuilder.DropColumn(
                name: "entitlement_delay_months",
                schema: "investments",
                table: "holding");

            migrationBuilder.AddCheckConstraint(
                name: "ck_holding_kind",
                schema: "investments",
                table: "holding",
                sql: "kind in ('euro_fund','fund','etf','equity','bond','structured','cash','other')");
        }
    }
}
