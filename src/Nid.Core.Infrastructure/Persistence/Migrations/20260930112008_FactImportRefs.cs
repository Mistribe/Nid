using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FactImportRefs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "import_hash",
                schema: "core",
                table: "valuation",
                type: "char(64)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "import_ref",
                schema: "core",
                table: "valuation",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "imported_by",
                schema: "core",
                table: "valuation",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "import_hash",
                schema: "core",
                table: "flow",
                type: "char(64)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "import_ref",
                schema: "core",
                table: "flow",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "imported_by",
                schema: "core",
                table: "flow",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_valuation_import_ref",
                schema: "core",
                table: "valuation",
                columns: new[] { "imported_by", "import_ref" },
                unique: true,
                filter: "import_ref is not null and retired_at is null");

            migrationBuilder.AddCheckConstraint(
                name: "ck_valuation_import",
                schema: "core",
                table: "valuation",
                sql: "(import_ref is null) = (import_hash is null) and (import_ref is null) = (imported_by is null)");

            migrationBuilder.CreateIndex(
                name: "ux_flow_import_ref",
                schema: "core",
                table: "flow",
                columns: new[] { "imported_by", "import_ref" },
                unique: true,
                filter: "import_ref is not null and retired_at is null");

            migrationBuilder.AddCheckConstraint(
                name: "ck_flow_import",
                schema: "core",
                table: "flow",
                sql: "(import_ref is null) = (import_hash is null) and (import_ref is null) = (imported_by is null)");

            migrationBuilder.AddForeignKey(
                name: "fk_flow_person_imported_by",
                schema: "core",
                table: "flow",
                column: "imported_by",
                principalSchema: "core",
                principalTable: "person",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_valuation_person_imported_by",
                schema: "core",
                table: "valuation",
                column: "imported_by",
                principalSchema: "core",
                principalTable: "person",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_flow_person_imported_by",
                schema: "core",
                table: "flow");

            migrationBuilder.DropForeignKey(
                name: "fk_valuation_person_imported_by",
                schema: "core",
                table: "valuation");

            migrationBuilder.DropIndex(
                name: "ux_valuation_import_ref",
                schema: "core",
                table: "valuation");

            migrationBuilder.DropCheckConstraint(
                name: "ck_valuation_import",
                schema: "core",
                table: "valuation");

            migrationBuilder.DropIndex(
                name: "ux_flow_import_ref",
                schema: "core",
                table: "flow");

            migrationBuilder.DropCheckConstraint(
                name: "ck_flow_import",
                schema: "core",
                table: "flow");

            migrationBuilder.DropColumn(
                name: "import_hash",
                schema: "core",
                table: "valuation");

            migrationBuilder.DropColumn(
                name: "import_ref",
                schema: "core",
                table: "valuation");

            migrationBuilder.DropColumn(
                name: "imported_by",
                schema: "core",
                table: "valuation");

            migrationBuilder.DropColumn(
                name: "import_hash",
                schema: "core",
                table: "flow");

            migrationBuilder.DropColumn(
                name: "import_ref",
                schema: "core",
                table: "flow");

            migrationBuilder.DropColumn(
                name: "imported_by",
                schema: "core",
                table: "flow");
        }
    }
}
