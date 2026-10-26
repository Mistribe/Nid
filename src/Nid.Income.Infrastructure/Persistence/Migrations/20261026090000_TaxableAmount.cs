using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Income.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TaxableAmount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "taxable_amount",
                schema: "income",
                table: "income_phase",
                type: "numeric(19,4)",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_income_phase_taxable",
                schema: "income",
                table: "income_phase",
                sql: "taxable_amount is null or taxable_amount > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_income_phase_taxable",
                schema: "income",
                table: "income_phase");

            migrationBuilder.DropColumn(
                name: "taxable_amount",
                schema: "income",
                table: "income_phase");
        }
    }
}
