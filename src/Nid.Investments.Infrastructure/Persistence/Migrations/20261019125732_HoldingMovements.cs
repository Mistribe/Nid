using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Investments.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HoldingMovements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "holding_movement",
                schema: "investments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    holding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    envelope_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_on = table.Column<LocalDate>(type: "date", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    units = table.Column<decimal>(type: "numeric(20,6)", nullable: true),
                    unit_price = table.Column<decimal>(type: "numeric(20,6)", nullable: true),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    envelope_flow_id = table.Column<Guid>(type: "uuid", nullable: true),
                    switch_ref = table.Column<string>(type: "text", nullable: true),
                    label = table.Column<string>(type: "text", nullable: true),
                    external_ref = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_holding_movement", x => x.id);
                    table.CheckConstraint("ck_holding_movement_amounts", "amount > 0 and (unit_price is null or unit_price >= 0)");
                    table.CheckConstraint("ck_holding_movement_kind", "kind in ('allocation_in','allocation_out','switch_in','switch_out','maturity','fee','interest','income')");
                    table.ForeignKey(
                        name: "fk_holding_movement_envelope_envelope_item_id",
                        column: x => x.envelope_item_id,
                        principalSchema: "investments",
                        principalTable: "envelope",
                        principalColumn: "item_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_holding_movement_holding_holding_id",
                        column: x => x.holding_id,
                        principalSchema: "investments",
                        principalTable: "holding",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_holding_movement_envelope_item_id_occurred_on",
                schema: "investments",
                table: "holding_movement",
                columns: new[] { "envelope_item_id", "occurred_on" });

            migrationBuilder.CreateIndex(
                name: "ix_holding_movement_holding_id",
                schema: "investments",
                table: "holding_movement",
                column: "holding_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "holding_movement",
                schema: "investments");
        }
    }
}
