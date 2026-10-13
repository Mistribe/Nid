using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.RealEstate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PropertyRental : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "property_recurring_cost",
                schema: "realestate",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    property_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    every_count = table.Column<int>(type: "integer", nullable: false),
                    every_unit = table.Column<string>(type: "text", nullable: false),
                    starts_on = table.Column<LocalDate>(type: "date", nullable: false),
                    ends_on = table.Column<LocalDate>(type: "date", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_property_recurring_cost", x => x.id);
                    table.CheckConstraint("ck_property_recurring_cost_amount", "amount > 0");
                    table.CheckConstraint("ck_property_recurring_cost_category", "category in ('property_tax','condo_fees','insurance','energy','maintenance','other')");
                    table.CheckConstraint("ck_property_recurring_cost_dates", "ends_on is null or ends_on > starts_on");
                    table.CheckConstraint("ck_property_recurring_cost_every", "every_count between 1 and 120 and every_unit in ('month','year')");
                });

            migrationBuilder.CreateTable(
                name: "property_tenancy",
                schema: "realestate",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    property_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "text", nullable: false),
                    starts_on = table.Column<LocalDate>(type: "date", nullable: false),
                    ends_on = table.Column<LocalDate>(type: "date", nullable: true),
                    rent_monthly = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    charges_monthly = table.Column<decimal>(type: "numeric(19,4)", nullable: false),
                    deposit = table.Column<decimal>(type: "numeric(19,4)", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_property_tenancy", x => x.id);
                    table.CheckConstraint("ck_property_tenancy_amounts", "rent_monthly > 0 and charges_monthly >= 0 and (deposit is null or deposit > 0)");
                    table.CheckConstraint("ck_property_tenancy_dates", "ends_on is null or ends_on > starts_on");
                });

            migrationBuilder.CreateIndex(
                name: "ix_property_recurring_cost_property_item_id",
                schema: "realestate",
                table: "property_recurring_cost",
                column: "property_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_property_tenancy_property_item_id",
                schema: "realestate",
                table: "property_tenancy",
                column: "property_item_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "property_recurring_cost",
                schema: "realestate");

            migrationBuilder.DropTable(
                name: "property_tenancy",
                schema: "realestate");
        }
    }
}
