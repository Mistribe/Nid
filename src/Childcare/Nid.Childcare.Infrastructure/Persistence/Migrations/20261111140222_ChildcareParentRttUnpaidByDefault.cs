using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Childcare.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChildcareParentRttUnpaidByDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string[]>(
                name: "unpaid_absences",
                schema: "childcare",
                table: "terms",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{parent_rtt,child_sick,carer_absent}'",
                oldClrType: typeof(string[]),
                oldType: "text[]",
                oldDefaultValueSql: "'{child_sick,carer_absent}'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string[]>(
                name: "unpaid_absences",
                schema: "childcare",
                table: "terms",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{child_sick,carer_absent}'",
                oldClrType: typeof(string[]),
                oldType: "text[]",
                oldDefaultValueSql: "'{parent_rtt,child_sick,carer_absent}'");
        }
    }
}
