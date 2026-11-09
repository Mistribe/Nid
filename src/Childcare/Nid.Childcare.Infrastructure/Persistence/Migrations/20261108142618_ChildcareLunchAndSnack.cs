using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Childcare.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChildcareLunchAndSnack : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_terms_amounts",
                schema: "childcare",
                table: "terms");

            migrationBuilder.RenameColumn(
                name: "meal",
                schema: "childcare",
                table: "weekday",
                newName: "lunch");

            migrationBuilder.RenameColumn(
                name: "meal_price",
                schema: "childcare",
                table: "terms",
                newName: "lunch_price");

            migrationBuilder.RenameColumn(
                name: "meal",
                schema: "childcare",
                table: "statement_day",
                newName: "lunch");

            migrationBuilder.AddColumn<bool>(
                name: "snack",
                schema: "childcare",
                table: "weekday",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "snack_price",
                schema: "childcare",
                table: "terms",
                type: "numeric(19,4)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "lunch_provider",
                schema: "childcare",
                table: "terms",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "nanny");

            migrationBuilder.AddColumn<string>(
                name: "snack_provider",
                schema: "childcare",
                table: "terms",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "nanny");

            migrationBuilder.AddColumn<bool>(
                name: "snack",
                schema: "childcare",
                table: "statement_day",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddCheckConstraint(
                name: "ck_terms_amounts",
                schema: "childcare",
                table: "terms",
                sql: "net_hourly_rate > 0 and overtime_rate >= 0 and upkeep_per9h >= 0 and (lunch_price is null or lunch_price >= 0) and (snack_price is null or snack_price >= 0) and (gross_hourly_rate is null or gross_hourly_rate > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_terms_meal_providers",
                schema: "childcare",
                table: "terms",
                sql: "lunch_provider in ('nanny', 'employer') and snack_provider in ('nanny', 'employer')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_terms_amounts",
                schema: "childcare",
                table: "terms");

            migrationBuilder.DropCheckConstraint(
                name: "ck_terms_meal_providers",
                schema: "childcare",
                table: "terms");

            migrationBuilder.DropColumn(
                name: "snack",
                schema: "childcare",
                table: "weekday");

            migrationBuilder.DropColumn(
                name: "snack_price",
                schema: "childcare",
                table: "terms");

            migrationBuilder.DropColumn(
                name: "lunch_provider",
                schema: "childcare",
                table: "terms");

            migrationBuilder.DropColumn(
                name: "snack_provider",
                schema: "childcare",
                table: "terms");

            migrationBuilder.DropColumn(
                name: "snack",
                schema: "childcare",
                table: "statement_day");

            migrationBuilder.RenameColumn(
                name: "lunch",
                schema: "childcare",
                table: "weekday",
                newName: "meal");

            migrationBuilder.RenameColumn(
                name: "lunch_price",
                schema: "childcare",
                table: "terms",
                newName: "meal_price");

            migrationBuilder.RenameColumn(
                name: "lunch",
                schema: "childcare",
                table: "statement_day",
                newName: "meal");

            migrationBuilder.AddCheckConstraint(
                name: "ck_terms_amounts",
                schema: "childcare",
                table: "terms",
                sql: "net_hourly_rate > 0 and overtime_rate >= 0 and upkeep_per9h >= 0 and (meal_price is null or meal_price >= 0) and (gross_hourly_rate is null or gross_hourly_rate > 0)");
        }
    }
}
