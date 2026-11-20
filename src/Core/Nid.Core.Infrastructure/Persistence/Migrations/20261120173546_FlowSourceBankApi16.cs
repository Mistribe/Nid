using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FlowSourceBankApi16 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_flow_source",
                schema: "core",
                table: "flow");

            migrationBuilder.AddCheckConstraint(
                name: "ck_flow_source",
                schema: "core",
                table: "flow",
                sql: "source in ('manual','import','bank_api')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_flow_source",
                schema: "core",
                table: "flow");

            migrationBuilder.AddCheckConstraint(
                name: "ck_flow_source",
                schema: "core",
                table: "flow",
                sql: "source in ('manual','import')");
        }
    }
}
