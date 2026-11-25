using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Assets.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class InitialAssets : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "assets");

        migrationBuilder.CreateTable(
            name: "asset",
            schema: "assets",
            columns: table => new
            {
                item_id = table.Column<Guid>(type: "uuid", nullable: false),
                kind = table.Column<string>(type: "text", nullable: false),
                purchase_price = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                purchased_on = table.Column<LocalDate>(type: "date", nullable: true),
                note = table.Column<string>(type: "text", nullable: true),
                weight_grams = table.Column<decimal>(type: "numeric(12,3)", nullable: true),
                fineness = table.Column<decimal>(type: "numeric(5,1)", nullable: true),
                import_ref = table.Column<string>(type: "text", nullable: true),
                import_hash = table.Column<string>(type: "char(64)", nullable: true),
                imported_by = table.Column<Guid>(type: "uuid", nullable: true),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_asset", x => x.item_id);
                table.CheckConstraint("ck_asset_import", "(import_ref is null) = (import_hash is null) and (import_ref is null) = (imported_by is null)");
                table.CheckConstraint("ck_asset_metal", "kind = 'precious_metal' or (weight_grams is null and fineness is null)");
                table.CheckConstraint("ck_asset_purchase", "(purchase_price is null) = (purchased_on is null)");
            });

        migrationBuilder.CreateIndex(
            name: "ix_asset_imported_by_import_ref",
            schema: "assets",
            table: "asset",
            columns: new[] { "imported_by", "import_ref" },
            unique: true,
            filter: "import_ref is not null");

        migrationBuilder.CreateIndex(
            name: "ix_asset_kind",
            schema: "assets",
            table: "asset",
            column: "kind");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "asset",
            schema: "assets");
    }
}
