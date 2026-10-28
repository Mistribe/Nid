using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TaxAssessments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tax_assessment",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tax_assessment", x => x.id);
                    table.CheckConstraint("ck_tax_assessment_year", "year between 2000 and 2100");
                });

            migrationBuilder.CreateTable(
                name: "tax_assessment_declarant",
                schema: "core",
                columns: table => new
                {
                    assessment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    rank = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tax_assessment_declarant", x => new { x.assessment_id, x.person_id });
                    table.CheckConstraint("ck_tax_assessment_declarant_rank", "rank in (1,2)");
                    table.ForeignKey(
                        name: "fk_tax_assessment_declarant_person_person_id",
                        column: x => x.person_id,
                        principalSchema: "core",
                        principalTable: "person",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tax_assessment_declarant_tax_assessment_assessment_id",
                        column: x => x.assessment_id,
                        principalSchema: "core",
                        principalTable: "tax_assessment",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tax_assessment_declarant_person_id",
                schema: "core",
                table: "tax_assessment_declarant",
                column: "person_id");

            migrationBuilder.CreateIndex(
                name: "ix_tax_assessment_declarant_year_person_id",
                schema: "core",
                table: "tax_assessment_declarant",
                columns: new[] { "year", "person_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tax_assessment_declarant",
                schema: "core");

            migrationBuilder.DropTable(
                name: "tax_assessment",
                schema: "core");
        }
    }
}
