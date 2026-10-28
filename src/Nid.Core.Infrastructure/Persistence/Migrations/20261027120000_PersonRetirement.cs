using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PersonRetirement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<LocalDate>(
                name: "birth_date",
                schema: "core",
                table: "person",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "retirement_age",
                schema: "core",
                table: "person",
                type: "integer",
                nullable: false,
                defaultValue: 64);

            migrationBuilder.AddCheckConstraint(
                name: "ck_person_retirement_age",
                schema: "core",
                table: "person",
                sql: "retirement_age between 50 and 75");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_person_retirement_age",
                schema: "core",
                table: "person");

            migrationBuilder.DropColumn(
                name: "birth_date",
                schema: "core",
                table: "person");

            migrationBuilder.DropColumn(
                name: "retirement_age",
                schema: "core",
                table: "person");
        }
    }
}
