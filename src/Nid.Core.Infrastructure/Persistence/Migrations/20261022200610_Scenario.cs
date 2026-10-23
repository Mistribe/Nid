using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Scenario : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "scenario",
                schema: "core",
                columns: table => new
                {
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    scope = table.Column<string>(type: "text", nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scenario", x => x.item_id);
                    table.CheckConstraint("ck_scenario_kind", "kind = 'property_purchase'");
                    table.CheckConstraint("ck_scenario_scope", "scope in ('me','household')");
                    table.ForeignKey(
                        name: "fk_scenario_item_item_id",
                        column: x => x.item_id,
                        principalSchema: "core",
                        principalTable: "item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "scenario",
                schema: "core");
        }
    }
}
