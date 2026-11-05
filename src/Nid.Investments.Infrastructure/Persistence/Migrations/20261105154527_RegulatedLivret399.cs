using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Investments.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RegulatedLivret399 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "regulated",
                schema: "investments",
                table: "envelope",
                type: "text",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_envelope_regulated",
                schema: "investments",
                table: "envelope",
                sql: "regulated is null or (kind = 'savings' and regulated in ('livret_a','ldds','lep','livret_jeune'))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_envelope_regulated",
                schema: "investments",
                table: "envelope");

            migrationBuilder.DropColumn(
                name: "regulated",
                schema: "investments",
                table: "envelope");
        }
    }
}
