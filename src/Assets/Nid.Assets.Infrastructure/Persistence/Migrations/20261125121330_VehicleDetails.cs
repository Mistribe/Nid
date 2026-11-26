using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Assets.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class VehicleDetails : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "energy",
            schema: "assets",
            table: "asset",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<short>(
            name: "fiscal_hp",
            schema: "assets",
            table: "asset",
            type: "smallint",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "mileage_km",
            schema: "assets",
            table: "asset",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<LocalDate>(
            name: "mileage_on",
            schema: "assets",
            table: "asset",
            type: "date",
            nullable: true);

        migrationBuilder.AddColumn<LocalDate>(
            name: "registered_on",
            schema: "assets",
            table: "asset",
            type: "date",
            nullable: true);

        migrationBuilder.AddCheckConstraint(
            name: "ck_asset_mileage",
            schema: "assets",
            table: "asset",
            sql: "(mileage_km is null) = (mileage_on is null)");

        migrationBuilder.AddCheckConstraint(
            name: "ck_asset_vehicle",
            schema: "assets",
            table: "asset",
            sql: "kind = 'vehicle' or (registered_on is null and mileage_km is null and mileage_on is null and fiscal_hp is null and energy is null)");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_asset_mileage",
            schema: "assets",
            table: "asset");

        migrationBuilder.DropCheckConstraint(
            name: "ck_asset_vehicle",
            schema: "assets",
            table: "asset");

        migrationBuilder.DropColumn(
            name: "energy",
            schema: "assets",
            table: "asset");

        migrationBuilder.DropColumn(
            name: "fiscal_hp",
            schema: "assets",
            table: "asset");

        migrationBuilder.DropColumn(
            name: "mileage_km",
            schema: "assets",
            table: "asset");

        migrationBuilder.DropColumn(
            name: "mileage_on",
            schema: "assets",
            table: "asset");

        migrationBuilder.DropColumn(
            name: "registered_on",
            schema: "assets",
            table: "asset");
    }
}
