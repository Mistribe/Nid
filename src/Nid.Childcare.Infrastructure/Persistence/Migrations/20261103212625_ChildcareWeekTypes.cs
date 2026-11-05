using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Childcare.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChildcareWeekTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "week_type",
                schema: "childcare",
                columns: table => new
                {
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    terms_from = table.Column<LocalDate>(type: "date", nullable: false),
                    rank = table.Column<short>(type: "smallint", nullable: false),
                    name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    weeks = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_week_type", x => new { x.item_id, x.terms_from, x.rank });
                    table.CheckConstraint("ck_week_type_weeks", "weeks >= 1");
                    table.ForeignKey(
                        name: "fk_week_type_terms_item_id_terms_from",
                        columns: x => new { x.item_id, x.terms_from },
                        principalSchema: "childcare",
                        principalTable: "terms",
                        principalColumns: new[] { "item_id", "from" },
                        onDelete: ReferentialAction.Cascade);
                });

            // Each existing revision becomes one week type holding its programmed weeks and its days.
            migrationBuilder.Sql("""
                insert into childcare.week_type (item_id, terms_from, rank, name, weeks)
                select item_id, "from", 0, 'Semaine type', programmed_weeks from childcare.terms;
                """);

            migrationBuilder.AddColumn<short>(
                name: "week_type",
                schema: "childcare",
                table: "weekday",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.DropForeignKey(
                name: "fk_weekday_terms_item_id_terms_from",
                schema: "childcare",
                table: "weekday");

            migrationBuilder.DropPrimaryKey(
                name: "pk_weekday",
                schema: "childcare",
                table: "weekday");

            migrationBuilder.AddPrimaryKey(
                name: "pk_weekday",
                schema: "childcare",
                table: "weekday",
                columns: new[] { "item_id", "terms_from", "week_type", "day" });

            migrationBuilder.AddForeignKey(
                name: "fk_weekday_week_type_item_id_terms_from_week_type",
                schema: "childcare",
                table: "weekday",
                columns: new[] { "item_id", "terms_from", "week_type" },
                principalSchema: "childcare",
                principalTable: "week_type",
                principalColumns: new[] { "item_id", "terms_from", "rank" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.CreateTable(
                name: "week_override",
                schema: "childcare",
                columns: table => new
                {
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    monday = table.Column<LocalDate>(type: "date", nullable: false),
                    week_type = table.Column<short>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_week_override", x => new { x.item_id, x.monday });
                    table.ForeignKey(
                        name: "fk_week_override_arrangement_item_id",
                        column: x => x.item_id,
                        principalSchema: "childcare",
                        principalTable: "arrangement",
                        principalColumn: "item_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.DropCheckConstraint(
                name: "ck_terms_weeks",
                schema: "childcare",
                table: "terms");

            migrationBuilder.DropColumn(
                name: "programmed_weeks",
                schema: "childcare",
                table: "terms");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_weekday_week_type_item_id_terms_from_week_type",
                schema: "childcare",
                table: "weekday");

            migrationBuilder.DropTable(
                name: "week_override",
                schema: "childcare");

            // The old model holds one schedule: the first week type's days stay, and the weeks add up.
            migrationBuilder.Sql("delete from childcare.weekday where week_type > 0;");

            migrationBuilder.DropPrimaryKey(
                name: "pk_weekday",
                schema: "childcare",
                table: "weekday");

            migrationBuilder.DropColumn(
                name: "week_type",
                schema: "childcare",
                table: "weekday");

            migrationBuilder.AddColumn<int>(
                name: "programmed_weeks",
                schema: "childcare",
                table: "terms",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("""
                update childcare.terms t set programmed_weeks =
                  (select sum(w.weeks) from childcare.week_type w where w.item_id = t.item_id and w.terms_from = t."from");
                """);

            migrationBuilder.DropTable(
                name: "week_type",
                schema: "childcare");

            migrationBuilder.AddPrimaryKey(
                name: "pk_weekday",
                schema: "childcare",
                table: "weekday",
                columns: new[] { "item_id", "terms_from", "day" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_terms_weeks",
                schema: "childcare",
                table: "terms",
                sql: "programmed_weeks between 1 and 46");

            migrationBuilder.AddForeignKey(
                name: "fk_weekday_terms_item_id_terms_from",
                schema: "childcare",
                table: "weekday",
                columns: new[] { "item_id", "terms_from" },
                principalSchema: "childcare",
                principalTable: "terms",
                principalColumns: new[] { "item_id", "from" },
                onDelete: ReferentialAction.Cascade);
        }
    }
}
