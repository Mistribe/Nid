using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Childcare.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChildcareDeclaredFigures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "declared_activity_days",
                schema: "childcare",
                table: "statement",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "declared_complementary_hours",
                schema: "childcare",
                table: "statement",
                type: "numeric(9,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "declared_meals",
                schema: "childcare",
                table: "statement",
                type: "numeric(19,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "declared_net_pay",
                schema: "childcare",
                table: "statement",
                type: "numeric(19,4)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "declared_normal_hours",
                schema: "childcare",
                table: "statement",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "declared_overtime_hours",
                schema: "childcare",
                table: "statement",
                type: "numeric(9,2)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "declared_paid_leave_days",
                schema: "childcare",
                table: "statement",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "declared_upkeep",
                schema: "childcare",
                table: "statement",
                type: "numeric(19,4)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "declared_activity_days",
                schema: "childcare",
                table: "statement");

            migrationBuilder.DropColumn(
                name: "declared_complementary_hours",
                schema: "childcare",
                table: "statement");

            migrationBuilder.DropColumn(
                name: "declared_meals",
                schema: "childcare",
                table: "statement");

            migrationBuilder.DropColumn(
                name: "declared_net_pay",
                schema: "childcare",
                table: "statement");

            migrationBuilder.DropColumn(
                name: "declared_normal_hours",
                schema: "childcare",
                table: "statement");

            migrationBuilder.DropColumn(
                name: "declared_overtime_hours",
                schema: "childcare",
                table: "statement");

            migrationBuilder.DropColumn(
                name: "declared_paid_leave_days",
                schema: "childcare",
                table: "statement");

            migrationBuilder.DropColumn(
                name: "declared_upkeep",
                schema: "childcare",
                table: "statement");
        }
    }
}
