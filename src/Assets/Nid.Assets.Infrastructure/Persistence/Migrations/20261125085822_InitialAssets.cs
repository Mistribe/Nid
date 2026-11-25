using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Assets.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class _20261125085822_InitialAssets : Migration
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
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_asset", x => x.item_id);
                table.CheckConstraint("ck_asset_metal", "kind = 'precious_metal' or (weight_grams is null and fineness is null)");
                table.CheckConstraint("ck_asset_purchase", "(purchase_price is null) = (purchased_on is null)");
            });

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
