using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Investments.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScpiKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_envelope_kind",
                schema: "investments",
                table: "envelope");

            migrationBuilder.AddCheckConstraint(
                name: "ck_envelope_kind",
                schema: "investments",
                table: "envelope",
                sql: "kind in ('checking','savings','pea','life_insurance','per','brokerage','other','tontine','employee_savings','collective_per','scpi')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_envelope_kind",
                schema: "investments",
                table: "envelope");

            migrationBuilder.AddCheckConstraint(
                name: "ck_envelope_kind",
                schema: "investments",
                table: "envelope",
                sql: "kind in ('checking','savings','pea','life_insurance','per','brokerage','other','tontine','employee_savings','collective_per')");
        }
    }
}
