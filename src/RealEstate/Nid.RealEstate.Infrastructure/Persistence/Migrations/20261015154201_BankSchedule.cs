using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.RealEstate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BankSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "schedule_source",
                schema: "realestate",
                table: "loan",
                type: "text",
                nullable: false,
                defaultValue: "computed");

            migrationBuilder.CreateTable(
                name: "loan_bank_schedule_row",
                schema: "realestate",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    loan_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    due_date = table.Column<LocalDate>(type: "date", nullable: false),
                    payment = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    interest = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    principal = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    insurance = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    crd = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    import_ref = table.Column<string>(type: "text", nullable: true),
                    import_hash = table.Column<string>(type: "char(64)", nullable: true),
                    imported_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_loan_bank_schedule_row", x => x.id);
                    table.CheckConstraint("ck_loan_bank_schedule_row_amounts", "payment >= 0 and interest >= 0 and principal >= 0 and (insurance is null or insurance >= 0) and crd >= 0");
                    table.CheckConstraint("ck_loan_bank_schedule_row_import", "(import_ref is null) = (import_hash is null) and (import_ref is null) = (imported_by is null)");
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_loan_schedule_source",
                schema: "realestate",
                table: "loan",
                sql: "schedule_source in ('computed','bank')");

            migrationBuilder.CreateIndex(
                name: "ix_loan_bank_schedule_row_imported_by_import_ref",
                schema: "realestate",
                table: "loan_bank_schedule_row",
                columns: new[] { "imported_by", "import_ref" },
                unique: true,
                filter: "import_ref is not null");

            migrationBuilder.CreateIndex(
                name: "ix_loan_bank_schedule_row_loan_item_id_due_date",
                schema: "realestate",
                table: "loan_bank_schedule_row",
                columns: new[] { "loan_item_id", "due_date" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "loan_bank_schedule_row",
                schema: "realestate");

            migrationBuilder.DropCheckConstraint(
                name: "ck_loan_schedule_source",
                schema: "realestate",
                table: "loan");

            migrationBuilder.DropColumn(
                name: "schedule_source",
                schema: "realestate",
                table: "loan");
        }
    }
}
