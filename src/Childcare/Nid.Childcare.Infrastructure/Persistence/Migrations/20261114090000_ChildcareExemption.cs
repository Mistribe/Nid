using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Childcare.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChildcareExemption : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_statement_amounts",
                schema: "childcare",
                table: "statement");

            migrationBuilder.AddColumn<decimal>(
                name: "exemption",
                schema: "childcare",
                table: "statement",
                type: "numeric(19,4)",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_statement_amounts",
                schema: "childcare",
                table: "statement",
                sql: "mileage >= 0 and (cmg is null or cmg >= 0) and (contributions is null or contributions >= 0) and (exemption is null or exemption >= 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_statement_amounts",
                schema: "childcare",
                table: "statement");

            migrationBuilder.DropColumn(
                name: "exemption",
                schema: "childcare",
                table: "statement");

            migrationBuilder.AddCheckConstraint(
                name: "ck_statement_amounts",
                schema: "childcare",
                table: "statement",
                sql: "mileage >= 0 and (cmg is null or cmg >= 0) and (contributions is null or contributions >= 0)");
        }
    }
}
