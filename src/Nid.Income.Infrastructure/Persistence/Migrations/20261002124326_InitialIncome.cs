using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Income.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialIncome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "income");

            migrationBuilder.CreateTable(
                name: "income_phase",
                schema: "income",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "text", nullable: false),
                    starts_on = table.Column<LocalDate>(type: "date", nullable: false),
                    ends_on = table.Column<LocalDate>(type: "date", nullable: true),
                    net_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    gross_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    every_count = table.Column<int>(type: "integer", nullable: false),
                    every_unit = table.Column<string>(type: "text", nullable: false),
                    max_cycles = table.Column<int>(type: "integer", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    import_ref = table.Column<string>(type: "text", nullable: true),
                    import_hash = table.Column<string>(type: "char(64)", nullable: true),
                    imported_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_income_phase", x => x.id);
                    table.CheckConstraint("ck_income_phase_amounts", "net_amount > 0 and (gross_amount is null or gross_amount > 0)");
                    table.CheckConstraint("ck_income_phase_dates", "ends_on is null or ends_on > starts_on");
                    table.CheckConstraint("ck_income_phase_every", "(every_unit = 'month' and every_count between 1 and 12) or (every_unit = 'year' and every_count = 1)");
                    table.CheckConstraint("ck_income_phase_import", "(import_ref is null) = (import_hash is null) and (import_ref is null) = (imported_by is null)");
                    table.CheckConstraint("ck_income_phase_label", "char_length(label) between 1 and 60");
                    table.CheckConstraint("ck_income_phase_max_cycles", "max_cycles is null or max_cycles >= 1");
                });

            migrationBuilder.CreateTable(
                name: "income_source",
                schema: "income",
                columns: table => new
                {
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    payer_label = table.Column<string>(type: "text", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    ended_on = table.Column<LocalDate>(type: "date", nullable: true),
                    import_ref = table.Column<string>(type: "text", nullable: true),
                    import_hash = table.Column<string>(type: "char(64)", nullable: true),
                    imported_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_income_source", x => x.item_id);
                    table.CheckConstraint("ck_income_source_import", "(import_ref is null) = (import_hash is null) and (import_ref is null) = (imported_by is null)");
                    table.CheckConstraint("ck_income_source_kind", "kind in ('salary','self_employment','pension','unemployment','family_allowance','social_benefit','alimony','other')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_income_phase_imported_by_import_ref",
                schema: "income",
                table: "income_phase",
                columns: new[] { "imported_by", "import_ref" },
                unique: true,
                filter: "import_ref is not null");

            migrationBuilder.CreateIndex(
                name: "ix_income_phase_source_item_id_label_starts_on",
                schema: "income",
                table: "income_phase",
                columns: new[] { "source_item_id", "label", "starts_on" });

            migrationBuilder.CreateIndex(
                name: "ix_income_source_imported_by_import_ref",
                schema: "income",
                table: "income_source",
                columns: new[] { "imported_by", "import_ref" },
                unique: true,
                filter: "import_ref is not null");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "income_phase",
                schema: "income");

            migrationBuilder.DropTable(
                name: "income_source",
                schema: "income");
        }
    }
}
