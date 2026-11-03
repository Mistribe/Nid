using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.RealEstate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PropertyReturnAssumptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "return_assumptions",
                schema: "realestate",
                table: "property",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "return_assumptions",
                schema: "realestate",
                table: "property");
        }
    }
}
