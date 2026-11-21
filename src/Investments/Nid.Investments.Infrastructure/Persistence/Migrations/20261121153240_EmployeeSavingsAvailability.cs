using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Investments.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EmployeeSavingsAvailability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "availability",
                schema: "investments",
                table: "envelope",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_envelope_availability",
                schema: "investments",
                table: "envelope",
                sql: "availability is null or kind = 'employee_savings'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_envelope_availability",
                schema: "investments",
                table: "envelope");

            migrationBuilder.DropColumn(
                name: "availability",
                schema: "investments",
                table: "envelope");
        }
    }
}
