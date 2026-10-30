using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.RealEstate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RecurringManagementFees : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_property_recurring_cost_category",
                schema: "realestate",
                table: "property_recurring_cost");

            migrationBuilder.AddCheckConstraint(
                name: "ck_property_recurring_cost_category",
                schema: "realestate",
                table: "property_recurring_cost",
                sql: "category in ('property_tax','condo_fees','insurance','energy','maintenance','management_fees','other')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_property_recurring_cost_category",
                schema: "realestate",
                table: "property_recurring_cost");

            migrationBuilder.AddCheckConstraint(
                name: "ck_property_recurring_cost_category",
                schema: "realestate",
                table: "property_recurring_cost",
                sql: "category in ('property_tax','condo_fees','insurance','energy','maintenance','other')");
        }
    }
}
