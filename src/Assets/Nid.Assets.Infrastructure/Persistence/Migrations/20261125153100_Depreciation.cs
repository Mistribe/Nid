using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Assets.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class Depreciation : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_asset_vehicle",
            schema: "assets",
            table: "asset");

        migrationBuilder.AddColumn<decimal>(
            name: "depreciation_rate",
            schema: "assets",
            table: "asset",
            type: "numeric(5,2)",
            nullable: true);

        migrationBuilder.AddCheckConstraint(
            name: "ck_asset_depreciation",
            schema: "assets",
            table: "asset",
            sql: "depreciation_rate is null or (depreciation_rate > 0 and depreciation_rate < 100)");

        migrationBuilder.AddCheckConstraint(
            name: "ck_asset_vehicle",
            schema: "assets",
            table: "asset",
            sql: "kind = 'vehicle' or (registered_on is null and mileage_km is null and mileage_on is null and fiscal_hp is null and energy is null and depreciation_rate is null)");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_asset_depreciation",
            schema: "assets",
            table: "asset");

        migrationBuilder.DropCheckConstraint(
            name: "ck_asset_vehicle",
            schema: "assets",
            table: "asset");

        migrationBuilder.DropColumn(
            name: "depreciation_rate",
            schema: "assets",
            table: "asset");

        migrationBuilder.AddCheckConstraint(
            name: "ck_asset_vehicle",
            schema: "assets",
            table: "asset",
            sql: "kind = 'vehicle' or (registered_on is null and mileage_km is null and mileage_on is null and fiscal_hp is null and energy is null)");
    }
}
