using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ShareLevel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "share_level",
                schema: "core",
                table: "item",
                type: "text",
                nullable: false,
                defaultValue: "view");

            migrationBuilder.AddCheckConstraint(
                name: "ck_item_share_level",
                schema: "core",
                table: "item",
                sql: "share_level = 'view' or (share_level = 'edit' and visibility = 'shared')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_item_share_level",
                schema: "core",
                table: "item");

            migrationBuilder.DropColumn(
                name: "share_level",
                schema: "core",
                table: "item");
        }
    }
}
