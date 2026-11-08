using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ValuationBankApi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_valuation_source",
                schema: "core",
                table: "valuation");

            migrationBuilder.DropCheckConstraint(
                name: "ck_proposal_created_via",
                schema: "core",
                table: "proposal");

            migrationBuilder.AddCheckConstraint(
                name: "ck_valuation_source",
                schema: "core",
                table: "valuation",
                sql: "source in ('manual','import','bank_api')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_proposal_created_via",
                schema: "core",
                table: "proposal",
                sql: "created_via in ('ui','api','mcp','job')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_valuation_source",
                schema: "core",
                table: "valuation");

            migrationBuilder.DropCheckConstraint(
                name: "ck_proposal_created_via",
                schema: "core",
                table: "proposal");

            migrationBuilder.AddCheckConstraint(
                name: "ck_valuation_source",
                schema: "core",
                table: "valuation",
                sql: "source in ('manual','import')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_proposal_created_via",
                schema: "core",
                table: "proposal",
                sql: "created_via in ('ui','api','mcp')");
        }
    }
}
