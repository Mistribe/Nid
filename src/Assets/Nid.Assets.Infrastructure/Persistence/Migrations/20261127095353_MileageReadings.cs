using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Assets.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class MileageReadings : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "asset_mileage_reading",
            schema: "assets",
            columns: table => new
            {
                item_id = table.Column<Guid>(type: "uuid", nullable: false),
                read_on = table.Column<LocalDate>(type: "date", nullable: false),
                km = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_asset_mileage_reading", x => new { x.item_id, x.read_on });
                table.CheckConstraint("ck_asset_mileage_reading_km", "km >= 0");
                table.ForeignKey(
                    name: "fk_asset_mileage_reading_asset_item_id",
                    column: x => x.item_id,
                    principalSchema: "assets",
                    principalTable: "asset",
                    principalColumn: "item_id",
                    onDelete: ReferentialAction.Cascade);
            });

        // #650: the mileage already recorded becomes the vehicle's first reading.
        migrationBuilder.Sql("""
            insert into assets.asset_mileage_reading (item_id, read_on, km)
            select item_id, mileage_on, mileage_km from assets.asset where mileage_km is not null and mileage_on is not null;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "asset_mileage_reading",
            schema: "assets");
    }
}
