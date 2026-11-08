using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Investments.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PremiumsBefore2017 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "premiums_before_2017",
                schema: "investments",
                table: "envelope",
                type: "numeric(19,4)",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_envelope_premiums_before_2017",
                schema: "investments",
                table: "envelope",
                sql: "premiums_before_2017 is null or premiums_before_2017 >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_envelope_premiums_before_2017",
                schema: "investments",
                table: "envelope");

            migrationBuilder.DropColumn(
                name: "premiums_before_2017",
                schema: "investments",
                table: "envelope");
        }
    }
}
