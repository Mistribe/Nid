using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProjectionScenarios : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_scenario_kind",
                schema: "core",
                table: "scenario");

            migrationBuilder.AddCheckConstraint(
                name: "ck_scenario_kind",
                schema: "core",
                table: "scenario",
                sql: "kind in ('property_purchase','projection')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_scenario_kind",
                schema: "core",
                table: "scenario");

            migrationBuilder.AddCheckConstraint(
                name: "ck_scenario_kind",
                schema: "core",
                table: "scenario",
                sql: "kind = 'property_purchase'");
        }
    }
}
