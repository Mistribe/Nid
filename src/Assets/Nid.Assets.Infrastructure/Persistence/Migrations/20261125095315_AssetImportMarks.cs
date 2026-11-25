using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Assets.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AssetImportMarks : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "import_hash",
            schema: "assets",
            table: "asset",
            type: "char(64)",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "import_ref",
            schema: "assets",
            table: "asset",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "imported_by",
            schema: "assets",
            table: "asset",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "ix_asset_imported_by_import_ref",
            schema: "assets",
            table: "asset",
            columns: new[] { "imported_by", "import_ref" },
            unique: true,
            filter: "import_ref is not null");

        migrationBuilder.AddCheckConstraint(
            name: "ck_asset_import",
            schema: "assets",
            table: "asset",
            sql: "(import_ref is null) = (import_hash is null) and (import_ref is null) = (imported_by is null)");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_asset_imported_by_import_ref",
            schema: "assets",
            table: "asset");

        migrationBuilder.DropCheckConstraint(
            name: "ck_asset_import",
            schema: "assets",
            table: "asset");

        migrationBuilder.DropColumn(
            name: "import_hash",
            schema: "assets",
            table: "asset");

        migrationBuilder.DropColumn(
            name: "import_ref",
            schema: "assets",
            table: "asset");

        migrationBuilder.DropColumn(
            name: "imported_by",
            schema: "assets",
            table: "asset");
    }
}
