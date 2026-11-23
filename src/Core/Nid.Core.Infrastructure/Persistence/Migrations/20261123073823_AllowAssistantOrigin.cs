using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Core.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class _20261123073823_AllowAssistantOrigin : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_valuation_created_via",
            schema: "core",
            table: "valuation");

        migrationBuilder.DropCheckConstraint(
            name: "ck_proposal_created_via",
            schema: "core",
            table: "proposal");

        migrationBuilder.DropCheckConstraint(
            name: "ck_flow_created_via",
            schema: "core",
            table: "flow");

        migrationBuilder.AddCheckConstraint(
            name: "ck_valuation_created_via",
            schema: "core",
            table: "valuation",
            sql: "created_via in ('ui','api','mcp','job','import','admin-cli','assistant')");

        migrationBuilder.AddCheckConstraint(
            name: "ck_proposal_created_via",
            schema: "core",
            table: "proposal",
            sql: "created_via in ('ui','api','mcp','job','assistant')");

        migrationBuilder.AddCheckConstraint(
            name: "ck_flow_created_via",
            schema: "core",
            table: "flow",
            sql: "created_via in ('ui','api','mcp','job','import','admin-cli','assistant')");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_valuation_created_via",
            schema: "core",
            table: "valuation");

        migrationBuilder.DropCheckConstraint(
            name: "ck_proposal_created_via",
            schema: "core",
            table: "proposal");

        migrationBuilder.DropCheckConstraint(
            name: "ck_flow_created_via",
            schema: "core",
            table: "flow");

        migrationBuilder.AddCheckConstraint(
            name: "ck_valuation_created_via",
            schema: "core",
            table: "valuation",
            sql: "created_via in ('ui','api','mcp','job','import','admin-cli')");

        migrationBuilder.AddCheckConstraint(
            name: "ck_proposal_created_via",
            schema: "core",
            table: "proposal",
            sql: "created_via in ('ui','api','mcp','job')");

        migrationBuilder.AddCheckConstraint(
            name: "ck_flow_created_via",
            schema: "core",
            table: "flow",
            sql: "created_via in ('ui','api','mcp','job','import','admin-cli')");
    }
}
