using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Childcare.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChildcareNanny : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "nanny",
                schema: "childcare",
                columns: table => new
                {
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    import_ref = table.Column<string>(type: "text", nullable: true),
                    import_hash = table.Column<string>(type: "char(64)", nullable: true),
                    imported_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_nanny", x => x.item_id);
                    table.CheckConstraint("ck_nanny_import", "(import_ref is null) = (import_hash is null) and (import_ref is null) = (imported_by is null)");
                });

            migrationBuilder.CreateTable(
                name: "nanny_month",
                schema: "childcare",
                columns: table => new
                {
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    month = table.Column<LocalDate>(type: "date", nullable: false),
                    paid = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    cmg = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    hours = table.Column<decimal>(type: "numeric(9,2)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_nanny_month", x => new { x.item_id, x.month });
                    table.CheckConstraint("ck_nanny_month_amounts", "paid > 0 and cmg >= 0 and cmg <= paid and (hours is null or hours > 0)");
                    table.CheckConstraint("ck_nanny_month_month", "extract(day from month) = 1");
                    table.ForeignKey(
                        name: "fk_nanny_month_nanny_item_id",
                        column: x => x.item_id,
                        principalSchema: "childcare",
                        principalTable: "nanny",
                        principalColumn: "item_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_nanny_imported_by_import_ref",
                schema: "childcare",
                table: "nanny",
                columns: new[] { "imported_by", "import_ref" },
                unique: true,
                filter: "import_ref is not null");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "nanny_month",
                schema: "childcare");

            migrationBuilder.DropTable(
                name: "nanny",
                schema: "childcare");
        }
    }
}
