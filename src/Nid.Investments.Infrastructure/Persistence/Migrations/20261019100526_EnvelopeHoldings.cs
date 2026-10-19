using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Investments.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnvelopeHoldings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "holding",
                schema: "investments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    envelope_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    isin = table.Column<string>(type: "char(12)", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    asset_class = table.Column<string>(type: "text", nullable: true),
                    issuer = table.Column<string>(type: "text", nullable: true),
                    risk_indicator = table.Column<int>(type: "integer", nullable: true),
                    opened_on = table.Column<LocalDate>(type: "date", nullable: true),
                    matures_on = table.Column<LocalDate>(type: "date", nullable: true),
                    entry_cost_pct = table.Column<decimal>(type: "numeric(9,4)", nullable: true),
                    closed_on = table.Column<LocalDate>(type: "date", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_holding", x => x.id);
                    table.CheckConstraint("ck_holding_asset_class", "asset_class is null or asset_class in ('equity','bond','money_market','mixed','real_estate','capital_guaranteed','other')");
                    table.CheckConstraint("ck_holding_dates", "closed_on is null or opened_on is null or closed_on >= opened_on");
                    table.CheckConstraint("ck_holding_entry_cost_pct", "entry_cost_pct is null or entry_cost_pct between 0 and 100");
                    table.CheckConstraint("ck_holding_kind", "kind in ('euro_fund','fund','etf','equity','bond','structured','cash','other')");
                    table.CheckConstraint("ck_holding_risk_indicator", "risk_indicator is null or risk_indicator between 1 and 7");
                    table.ForeignKey(
                        name: "fk_holding_envelope_envelope_item_id",
                        column: x => x.envelope_item_id,
                        principalSchema: "investments",
                        principalTable: "envelope",
                        principalColumn: "item_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "holding_snapshot",
                schema: "investments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    envelope_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    as_of = table.Column<LocalDate>(type: "date", nullable: false),
                    source_label = table.Column<string>(type: "text", nullable: true),
                    complete = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_holding_snapshot", x => x.id);
                    table.ForeignKey(
                        name: "fk_holding_snapshot_envelope_envelope_item_id",
                        column: x => x.envelope_item_id,
                        principalSchema: "investments",
                        principalTable: "envelope",
                        principalColumn: "item_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "holding_snapshot_line",
                schema: "investments",
                columns: table => new
                {
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    holding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    envelope_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    units = table.Column<decimal>(type: "numeric(20,6)", nullable: true),
                    unit_price = table.Column<decimal>(type: "numeric(20,6)", nullable: true),
                    value = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    cost_basis_per_unit = table.Column<decimal>(type: "numeric(20,6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_holding_snapshot_line", x => new { x.snapshot_id, x.holding_id });
                    table.CheckConstraint("ck_holding_snapshot_line_amounts", "value >= 0 and (units is null or units >= 0) and (unit_price is null or unit_price >= 0) and (cost_basis_per_unit is null or cost_basis_per_unit >= 0)");
                    table.ForeignKey(
                        name: "fk_holding_snapshot_line_holding_holding_id",
                        column: x => x.holding_id,
                        principalSchema: "investments",
                        principalTable: "holding",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_holding_snapshot_line_holding_snapshot_snapshot_id",
                        column: x => x.snapshot_id,
                        principalSchema: "investments",
                        principalTable: "holding_snapshot",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_holding_envelope_item_id_isin",
                schema: "investments",
                table: "holding",
                columns: new[] { "envelope_item_id", "isin" },
                unique: true,
                filter: "isin is not null");

            migrationBuilder.CreateIndex(
                name: "ix_holding_snapshot_envelope_item_id_as_of",
                schema: "investments",
                table: "holding_snapshot",
                columns: new[] { "envelope_item_id", "as_of" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_holding_snapshot_line_holding_id",
                schema: "investments",
                table: "holding_snapshot_line",
                column: "holding_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "holding_snapshot_line",
                schema: "investments");

            migrationBuilder.DropTable(
                name: "holding",
                schema: "investments");

            migrationBuilder.DropTable(
                name: "holding_snapshot",
                schema: "investments");
        }
    }
}
