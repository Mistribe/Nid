using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.RealEstate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenancyIdentities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "landlord_address",
                schema: "realestate",
                table: "property_tenancy",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "landlord_name",
                schema: "realestate",
                table: "property_tenancy",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tenant_names",
                schema: "realestate",
                table: "property_tenancy",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "postal_address",
                schema: "realestate",
                table: "property",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "landlord_address",
                schema: "realestate",
                table: "property_tenancy");

            migrationBuilder.DropColumn(
                name: "landlord_name",
                schema: "realestate",
                table: "property_tenancy");

            migrationBuilder.DropColumn(
                name: "tenant_names",
                schema: "realestate",
                table: "property_tenancy");

            migrationBuilder.DropColumn(
                name: "postal_address",
                schema: "realestate",
                table: "property");
        }
    }
}
