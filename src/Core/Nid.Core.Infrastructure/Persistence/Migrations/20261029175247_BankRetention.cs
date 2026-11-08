using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BankRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "bank_retention_months",
                schema: "core",
                table: "household_settings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_household_settings_bank_retention",
                schema: "core",
                table: "household_settings",
                sql: "bank_retention_months between 1 and 600");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_household_settings_bank_retention",
                schema: "core",
                table: "household_settings");

            migrationBuilder.DropColumn(
                name: "bank_retention_months",
                schema: "core",
                table: "household_settings");
        }
    }
}
