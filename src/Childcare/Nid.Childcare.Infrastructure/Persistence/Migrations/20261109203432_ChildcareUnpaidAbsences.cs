using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Childcare.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChildcareUnpaidAbsences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_statement_day_absence",
                schema: "childcare",
                table: "statement_day");

            migrationBuilder.AddColumn<string[]>(
                name: "unpaid_absences",
                schema: "childcare",
                table: "terms",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{child_sick,carer_absent}'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_statement_day_absence",
                schema: "childcare",
                table: "statement_day",
                sql: "absence is null or absence in ('family', 'parent_rtt', 'child_sick', 'carer_absent', 'holiday', 'paid_leave')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_statement_day_absence",
                schema: "childcare",
                table: "statement_day");

            migrationBuilder.DropColumn(
                name: "unpaid_absences",
                schema: "childcare",
                table: "terms");

            migrationBuilder.AddCheckConstraint(
                name: "ck_statement_day_absence",
                schema: "childcare",
                table: "statement_day",
                sql: "absence is null or absence in ('family', 'parent_rtt', 'child_sick', 'carer_absent', 'holiday')");
        }
    }
}
