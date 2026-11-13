using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Childcare.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChildcareFullYear : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The contracts made before the full year existed are incomplete years.
            migrationBuilder.AddColumn<bool>(
                name: "full_year",
                schema: "childcare",
                table: "terms",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "paid_leave_taken",
                schema: "childcare",
                table: "statement",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "full_year",
                schema: "childcare",
                table: "terms");

            migrationBuilder.DropColumn(
                name: "paid_leave_taken",
                schema: "childcare",
                table: "statement");
        }
    }
}
