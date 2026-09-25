using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Subscriptions.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProviderIcons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "icon_asset",
                schema: "subscriptions",
                table: "provider",
                type: "char(64)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "icon_source",
                schema: "subscriptions",
                table: "provider",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "icon_url",
                schema: "subscriptions",
                table: "provider",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_provider_icon_asset",
                schema: "subscriptions",
                table: "provider",
                column: "icon_asset");

            migrationBuilder.AddCheckConstraint(
                name: "ck_provider_icon",
                schema: "subscriptions",
                table: "provider",
                sql: "(icon_asset is null) = (icon_source is null)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_provider_icon_asset",
                schema: "subscriptions",
                table: "provider",
                sql: "icon_asset ~ '^[0-9a-f]{64}$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_provider_icon_url",
                schema: "subscriptions",
                table: "provider",
                sql: "char_length(icon_url) <= 2048");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_provider_icon_asset",
                schema: "subscriptions",
                table: "provider");

            migrationBuilder.DropCheckConstraint(
                name: "ck_provider_icon",
                schema: "subscriptions",
                table: "provider");

            migrationBuilder.DropCheckConstraint(
                name: "ck_provider_icon_asset",
                schema: "subscriptions",
                table: "provider");

            migrationBuilder.DropCheckConstraint(
                name: "ck_provider_icon_url",
                schema: "subscriptions",
                table: "provider");

            migrationBuilder.DropColumn(
                name: "icon_asset",
                schema: "subscriptions",
                table: "provider");

            migrationBuilder.DropColumn(
                name: "icon_source",
                schema: "subscriptions",
                table: "provider");

            migrationBuilder.DropColumn(
                name: "icon_url",
                schema: "subscriptions",
                table: "provider");
        }
    }
}
