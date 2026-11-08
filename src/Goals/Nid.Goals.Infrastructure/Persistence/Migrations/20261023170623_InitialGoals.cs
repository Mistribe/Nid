using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Goals.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialGoals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "goals");

            migrationBuilder.CreateTable(
                name: "goal",
                schema: "goals",
                columns: table => new
                {
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    target_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    months = table.Column<int>(type: "integer", nullable: true),
                    target_date = table.Column<LocalDate>(type: "date", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    import_ref = table.Column<string>(type: "text", nullable: true),
                    import_hash = table.Column<string>(type: "char(64)", nullable: true),
                    imported_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_goal", x => x.item_id);
                    table.CheckConstraint("ck_goal_import", "(import_ref is null) = (import_hash is null) and (import_ref is null) = (imported_by is null)");
                    table.CheckConstraint("ck_goal_shape", "(kind = 'savings' and target_amount > 0 and months is null) or (kind = 'emergency_fund' and months between 1 and 24 and target_amount is null)");
                });

            migrationBuilder.CreateTable(
                name: "funding",
                schema: "goals",
                columns: table => new
                {
                    goal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    envelope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    share_bp = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_funding", x => new { x.goal_id, x.envelope_id });
                    table.CheckConstraint("ck_funding_share", "share_bp between 1 and 10000");
                    table.ForeignKey(
                        name: "fk_funding_goal_goal_id",
                        column: x => x.goal_id,
                        principalSchema: "goals",
                        principalTable: "goal",
                        principalColumn: "item_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "progress_entry",
                schema: "goals",
                columns: table => new
                {
                    goal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    on = table.Column<LocalDate>(type: "date", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_progress_entry", x => new { x.goal_id, x.on });
                    table.CheckConstraint("ck_progress_entry_amount", "amount >= 0");
                    table.ForeignKey(
                        name: "fk_progress_entry_goal_goal_id",
                        column: x => x.goal_id,
                        principalSchema: "goals",
                        principalTable: "goal",
                        principalColumn: "item_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_funding_envelope_id",
                schema: "goals",
                table: "funding",
                column: "envelope_id");

            migrationBuilder.CreateIndex(
                name: "ix_goal_imported_by_import_ref",
                schema: "goals",
                table: "goal",
                columns: new[] { "imported_by", "import_ref" },
                unique: true,
                filter: "import_ref is not null");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "funding",
                schema: "goals");

            migrationBuilder.DropTable(
                name: "progress_entry",
                schema: "goals");

            migrationBuilder.DropTable(
                name: "goal",
                schema: "goals");
        }
    }
}
