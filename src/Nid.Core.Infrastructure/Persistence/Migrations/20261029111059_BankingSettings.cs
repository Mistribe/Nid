using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BankingSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "banking_enabled",
                schema: "core",
                table: "household_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "enable_banking_app_id",
                schema: "core",
                table: "household_settings",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "banking_enabled",
                schema: "core",
                table: "household_settings");

            migrationBuilder.DropColumn(
                name: "enable_banking_app_id",
                schema: "core",
                table: "household_settings");
        }
    }
}
