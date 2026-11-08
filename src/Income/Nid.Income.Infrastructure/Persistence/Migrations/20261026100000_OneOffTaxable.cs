using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Income.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OneOffTaxable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "one_off_taxable",
                schema: "income",
                columns: table => new
                {
                    flow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    taxable_amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_one_off_taxable", x => x.flow_id);
                    table.CheckConstraint("ck_one_off_taxable_amount", "taxable_amount > 0");
                    table.ForeignKey(
                        name: "fk_one_off_taxable_income_source_source_id",
                        column: x => x.source_id,
                        principalSchema: "income",
                        principalTable: "income_source",
                        principalColumn: "item_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_one_off_taxable_source_id",
                schema: "income",
                table: "one_off_taxable",
                column: "source_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "one_off_taxable",
                schema: "income");
        }
    }
}
