using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Childcare.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChildcareEndOfContract : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<LocalDate>(
                name: "end_notified",
                schema: "childcare",
                table: "arrangement",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "end_reason",
                schema: "childcare",
                table: "arrangement",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "leave_indemnity",
                schema: "childcare",
                table: "statement",
                type: "numeric(19,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "regularisation",
                schema: "childcare",
                table: "statement",
                type: "numeric(19,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "rupture_indemnity",
                schema: "childcare",
                table: "statement",
                type: "numeric(19,4)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "end_notified",
                schema: "childcare",
                table: "arrangement");

            migrationBuilder.DropColumn(
                name: "end_reason",
                schema: "childcare",
                table: "arrangement");

            migrationBuilder.DropColumn(
                name: "leave_indemnity",
                schema: "childcare",
                table: "statement");

            migrationBuilder.DropColumn(
                name: "regularisation",
                schema: "childcare",
                table: "statement");

            migrationBuilder.DropColumn(
                name: "rupture_indemnity",
                schema: "childcare",
                table: "statement");
        }
    }
}
