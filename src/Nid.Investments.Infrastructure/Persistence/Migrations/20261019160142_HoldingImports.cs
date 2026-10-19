using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Investments.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HoldingImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "import_hash",
                schema: "investments",
                table: "holding_snapshot",
                type: "char(64)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "import_ref",
                schema: "investments",
                table: "holding_snapshot",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "imported_by",
                schema: "investments",
                table: "holding_snapshot",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "import_hash",
                schema: "investments",
                table: "holding_movement",
                type: "char(64)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "import_ref",
                schema: "investments",
                table: "holding_movement",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "imported_by",
                schema: "investments",
                table: "holding_movement",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "import_hash",
                schema: "investments",
                table: "holding",
                type: "char(64)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "import_ref",
                schema: "investments",
                table: "holding",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "imported_by",
                schema: "investments",
                table: "holding",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_holding_snapshot_imported_by_import_ref",
                schema: "investments",
                table: "holding_snapshot",
                columns: new[] { "imported_by", "import_ref" },
                unique: true,
                filter: "import_ref is not null");

            migrationBuilder.AddCheckConstraint(
                name: "ck_holding_snapshot_import",
                schema: "investments",
                table: "holding_snapshot",
                sql: "(import_ref is null) = (import_hash is null) and (import_ref is null) = (imported_by is null)");

            migrationBuilder.CreateIndex(
                name: "ix_holding_movement_imported_by_import_ref",
                schema: "investments",
                table: "holding_movement",
                columns: new[] { "imported_by", "import_ref" },
                unique: true,
                filter: "import_ref is not null");

            migrationBuilder.AddCheckConstraint(
                name: "ck_holding_movement_import",
                schema: "investments",
                table: "holding_movement",
                sql: "(import_ref is null) = (import_hash is null) and (import_ref is null) = (imported_by is null)");

            migrationBuilder.CreateIndex(
                name: "ix_holding_imported_by_import_ref",
                schema: "investments",
                table: "holding",
                columns: new[] { "imported_by", "import_ref" },
                unique: true,
                filter: "import_ref is not null");

            migrationBuilder.AddCheckConstraint(
                name: "ck_holding_import",
                schema: "investments",
                table: "holding",
                sql: "(import_ref is null) = (import_hash is null) and (import_ref is null) = (imported_by is null)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_holding_snapshot_imported_by_import_ref",
                schema: "investments",
                table: "holding_snapshot");

            migrationBuilder.DropCheckConstraint(
                name: "ck_holding_snapshot_import",
                schema: "investments",
                table: "holding_snapshot");

            migrationBuilder.DropIndex(
                name: "ix_holding_movement_imported_by_import_ref",
                schema: "investments",
                table: "holding_movement");

            migrationBuilder.DropCheckConstraint(
                name: "ck_holding_movement_import",
                schema: "investments",
                table: "holding_movement");

            migrationBuilder.DropIndex(
                name: "ix_holding_imported_by_import_ref",
                schema: "investments",
                table: "holding");

            migrationBuilder.DropCheckConstraint(
                name: "ck_holding_import",
                schema: "investments",
                table: "holding");

            migrationBuilder.DropColumn(
                name: "import_hash",
                schema: "investments",
                table: "holding_snapshot");

            migrationBuilder.DropColumn(
                name: "import_ref",
                schema: "investments",
                table: "holding_snapshot");

            migrationBuilder.DropColumn(
                name: "imported_by",
                schema: "investments",
                table: "holding_snapshot");

            migrationBuilder.DropColumn(
                name: "import_hash",
                schema: "investments",
                table: "holding_movement");

            migrationBuilder.DropColumn(
                name: "import_ref",
                schema: "investments",
                table: "holding_movement");

            migrationBuilder.DropColumn(
                name: "imported_by",
                schema: "investments",
                table: "holding_movement");

            migrationBuilder.DropColumn(
                name: "import_hash",
                schema: "investments",
                table: "holding");

            migrationBuilder.DropColumn(
                name: "import_ref",
                schema: "investments",
                table: "holding");

            migrationBuilder.DropColumn(
                name: "imported_by",
                schema: "investments",
                table: "holding");
        }
    }
}
