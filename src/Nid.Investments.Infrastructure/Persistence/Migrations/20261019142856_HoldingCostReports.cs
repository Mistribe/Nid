using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Investments.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HoldingCostReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "uc_share_floor_pct",
                schema: "investments",
                table: "envelope",
                type: "numeric(9,4)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "holding_cost_report",
                schema: "investments",
                columns: table => new
                {
                    holding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    envelope_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    gross_return_pct = table.Column<decimal>(type: "numeric(9,4)", nullable: false),
                    total_fees_pct = table.Column<decimal>(type: "numeric(9,4)", nullable: false),
                    net_return_pct = table.Column<decimal>(type: "numeric(9,4)", nullable: false),
                    contract_fees_pct = table.Column<decimal>(type: "numeric(9,4)", nullable: true),
                    support_fees_pct = table.Column<decimal>(type: "numeric(9,4)", nullable: true),
                    retrocession_pct = table.Column<decimal>(type: "numeric(9,4)", nullable: true),
                    net_annualized_5y_pct = table.Column<decimal>(type: "numeric(9,4)", nullable: true),
                    source_label = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_holding_cost_report", x => new { x.holding_id, x.year });
                    table.CheckConstraint("ck_holding_cost_report_year", "year between 1900 and 2200");
                    table.ForeignKey(
                        name: "fk_holding_cost_report_envelope_envelope_item_id",
                        column: x => x.envelope_item_id,
                        principalSchema: "investments",
                        principalTable: "envelope",
                        principalColumn: "item_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_holding_cost_report_holding_holding_id",
                        column: x => x.holding_id,
                        principalSchema: "investments",
                        principalTable: "holding",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_envelope_uc_share_floor_pct",
                schema: "investments",
                table: "envelope",
                sql: "uc_share_floor_pct is null or uc_share_floor_pct between 0 and 100");

            migrationBuilder.CreateIndex(
                name: "ix_holding_cost_report_envelope_item_id",
                schema: "investments",
                table: "holding_cost_report",
                column: "envelope_item_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "holding_cost_report",
                schema: "investments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_envelope_uc_share_floor_pct",
                schema: "investments",
                table: "envelope");

            migrationBuilder.DropColumn(
                name: "uc_share_floor_pct",
                schema: "investments",
                table: "envelope");
        }
    }
}
