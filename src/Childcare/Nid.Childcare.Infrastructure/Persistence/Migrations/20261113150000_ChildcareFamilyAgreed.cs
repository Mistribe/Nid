using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Childcare.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChildcareFamilyAgreed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_statement_day_absence",
                schema: "childcare",
                table: "statement_day");

            migrationBuilder.AddCheckConstraint(
                name: "ck_statement_day_absence",
                schema: "childcare",
                table: "statement_day",
                sql: "absence is null or absence in ('family', 'parent_rtt', 'child_sick', 'carer_absent', 'holiday', 'paid_leave', 'carer_training', 'family_agreed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_statement_day_absence",
                schema: "childcare",
                table: "statement_day");

            migrationBuilder.AddCheckConstraint(
                name: "ck_statement_day_absence",
                schema: "childcare",
                table: "statement_day",
                sql: "absence is null or absence in ('family', 'parent_rtt', 'child_sick', 'carer_absent', 'holiday', 'paid_leave', 'carer_training')");
        }
    }
}
