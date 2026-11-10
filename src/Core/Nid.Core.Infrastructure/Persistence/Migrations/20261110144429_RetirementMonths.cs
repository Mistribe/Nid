using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RetirementMonths : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "retirement_months",
                schema: "core",
                table: "person",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "ck_person_retirement_months",
                schema: "core",
                table: "person",
                sql: "retirement_months between 0 and 11");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_person_retirement_months",
                schema: "core",
                table: "person");

            migrationBuilder.DropColumn(
                name: "retirement_months",
                schema: "core",
                table: "person");
        }
    }
}
