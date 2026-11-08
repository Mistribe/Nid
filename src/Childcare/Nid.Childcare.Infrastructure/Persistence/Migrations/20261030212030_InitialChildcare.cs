using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Childcare.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialChildcare : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "childcare");

            migrationBuilder.CreateTable(
                name: "arrangement",
                schema: "childcare",
                columns: table => new
                {
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    child_id = table.Column<Guid>(type: "uuid", nullable: false),
                    carer_name = table.Column<string>(type: "text", nullable: false),
                    carer_pajemploi = table.Column<string>(type: "text", nullable: true),
                    carer_approval = table.Column<string>(type: "text", nullable: true),
                    import_ref = table.Column<string>(type: "text", nullable: true),
                    import_hash = table.Column<string>(type: "char(64)", nullable: true),
                    imported_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_arrangement", x => x.item_id);
                    table.CheckConstraint("ck_arrangement_import", "(import_ref is null) = (import_hash is null) and (import_ref is null) = (imported_by is null)");
                });

            migrationBuilder.CreateTable(
                name: "gap",
                schema: "childcare",
                columns: table => new
                {
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    monday = table.Column<LocalDate>(type: "date", nullable: false),
                    weeks = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_gap", x => new { x.item_id, x.monday });
                    table.CheckConstraint("ck_gap_weeks", "weeks >= 1");
                    table.ForeignKey(
                        name: "fk_gap_arrangement_item_id",
                        column: x => x.item_id,
                        principalSchema: "childcare",
                        principalTable: "arrangement",
                        principalColumn: "item_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "terms",
                schema: "childcare",
                columns: table => new
                {
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from = table.Column<LocalDate>(type: "date", nullable: false),
                    to = table.Column<LocalDate>(type: "date", nullable: true),
                    net_hourly_rate = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    gross_hourly_rate = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    overtime_rate = table.Column<decimal>(type: "numeric(9,4)", nullable: false),
                    upkeep_per9h = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    meal_price = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    programmed_weeks = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_terms", x => new { x.item_id, x.from });
                    table.CheckConstraint("ck_terms_amounts", "net_hourly_rate > 0 and overtime_rate >= 0 and upkeep_per9h >= 0 and (meal_price is null or meal_price >= 0) and (gross_hourly_rate is null or gross_hourly_rate > 0)");
                    table.CheckConstraint("ck_terms_dates", "\"to\" is null or \"to\" >= \"from\"");
                    table.CheckConstraint("ck_terms_weeks", "programmed_weeks between 1 and 46");
                    table.ForeignKey(
                        name: "fk_terms_arrangement_item_id",
                        column: x => x.item_id,
                        principalSchema: "childcare",
                        principalTable: "arrangement",
                        principalColumn: "item_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "weekday",
                schema: "childcare",
                columns: table => new
                {
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    terms_from = table.Column<LocalDate>(type: "date", nullable: false),
                    day = table.Column<short>(type: "smallint", nullable: false),
                    arrival = table.Column<LocalTime>(type: "time", nullable: false),
                    departure = table.Column<LocalTime>(type: "time", nullable: false),
                    meal = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_weekday", x => new { x.item_id, x.terms_from, x.day });
                    table.CheckConstraint("ck_weekday_day", "day between 1 and 7");
                    table.CheckConstraint("ck_weekday_hours", "departure > arrival");
                    table.ForeignKey(
                        name: "fk_weekday_terms_item_id_terms_from",
                        columns: x => new { x.item_id, x.terms_from },
                        principalSchema: "childcare",
                        principalTable: "terms",
                        principalColumns: new[] { "item_id", "from" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_arrangement_child_id",
                schema: "childcare",
                table: "arrangement",
                column: "child_id");

            migrationBuilder.CreateIndex(
                name: "ix_arrangement_imported_by_import_ref",
                schema: "childcare",
                table: "arrangement",
                columns: new[] { "imported_by", "import_ref" },
                unique: true,
                filter: "import_ref is not null");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "gap",
                schema: "childcare");

            migrationBuilder.DropTable(
                name: "weekday",
                schema: "childcare");

            migrationBuilder.DropTable(
                name: "terms",
                schema: "childcare");

            migrationBuilder.DropTable(
                name: "arrangement",
                schema: "childcare");
        }
    }
}
