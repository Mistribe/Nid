using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Investments.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SnapshotUnitsPrintedPrecision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "units",
                schema: "investments",
                table: "holding_snapshot_line",
                type: "numeric",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(20,6)",
                oldNullable: true);

            // Issue #46: units stored before this were padded to 6 decimals; their trailing zeros say nothing of the print. trim_scale
            // keeps the value and drops them, so 1.185000 reads 1.185 again (a print of 1,200 now reads 1.2: a looser tolerance, never a
            // wrong figure).
            migrationBuilder.Sql("update investments.holding_snapshot_line set units = trim_scale(units) where units is not null;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "units",
                schema: "investments",
                table: "holding_snapshot_line",
                type: "numeric(20,6)",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric",
                oldNullable: true);
        }
    }
}
