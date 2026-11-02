using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Childcare.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChildcareStatements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "statement",
                schema: "childcare",
                columns: table => new
                {
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    month = table.Column<LocalDate>(type: "date", nullable: false),
                    validated = table.Column<bool>(type: "boolean", nullable: false),
                    mileage = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    cmg = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    validated_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    normal_hours = table.Column<int>(type: "integer", nullable: true),
                    activity_days = table.Column<int>(type: "integer", nullable: true),
                    paid_leave_days = table.Column<int>(type: "integer", nullable: true),
                    complementary_hours = table.Column<decimal>(type: "numeric(9,2)", nullable: true),
                    overtime_hours = table.Column<decimal>(type: "numeric(9,2)", nullable: true),
                    net_pay = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    upkeep = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    meals = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    base_pay = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    deduction = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    extra_pay = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    paid_leave_pay = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    cost = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    tax_base = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    child_sick_deducted = table.Column<int>(type: "integer", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_statement", x => new { x.item_id, x.month });
                    table.CheckConstraint("ck_statement_amounts", "mileage >= 0 and (cmg is null or cmg >= 0)");
                    table.CheckConstraint("ck_statement_figures", "validated = (validated_at is not null) and validated = (net_pay is not null) and validated = (cost is not null) and validated = (child_sick_deducted is not null)");
                    table.CheckConstraint("ck_statement_month", "extract(day from month) = 1");
                    table.ForeignKey(
                        name: "fk_statement_arrangement_item_id",
                        column: x => x.item_id,
                        principalSchema: "childcare",
                        principalTable: "arrangement",
                        principalColumn: "item_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "statement_day",
                schema: "childcare",
                columns: table => new
                {
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date = table.Column<LocalDate>(type: "date", nullable: false),
                    month = table.Column<LocalDate>(type: "date", nullable: false),
                    planned = table.Column<bool>(type: "boolean", nullable: false),
                    present = table.Column<bool>(type: "boolean", nullable: false),
                    arrival = table.Column<LocalTime>(type: "time", nullable: true),
                    departure = table.Column<LocalTime>(type: "time", nullable: true),
                    meal = table.Column<bool>(type: "boolean", nullable: false),
                    absence = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_statement_day", x => new { x.item_id, x.date });
                    table.CheckConstraint("ck_statement_day_absence", "absence is null or absence in ('family', 'parent_rtt', 'child_sick', 'carer_absent', 'holiday')");
                    table.CheckConstraint("ck_statement_day_hours", "not present or (arrival is not null and departure > arrival)");
                    table.ForeignKey(
                        name: "fk_statement_day_statement_item_id_month",
                        columns: x => new { x.item_id, x.month },
                        principalSchema: "childcare",
                        principalTable: "statement",
                        principalColumns: new[] { "item_id", "month" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_statement_day_item_id_month",
                schema: "childcare",
                table: "statement_day",
                columns: new[] { "item_id", "month" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "statement_day",
                schema: "childcare");

            migrationBuilder.DropTable(
                name: "statement",
                schema: "childcare");
        }
    }
}
