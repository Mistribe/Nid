using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Investments.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SecurityBreakdown : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "security_breakdown",
                schema: "investments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    isin = table.Column<string>(type: "char(12)", nullable: false),
                    as_of = table.Column<LocalDate>(type: "date", nullable: false),
                    regions = table.Column<string>(type: "jsonb", nullable: false),
                    sectors = table.Column<string>(type: "jsonb", nullable: true),
                    document_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recorded_by = table.Column<Guid>(type: "uuid", nullable: false),
                    recorded_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_security_breakdown", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_security_breakdown_isin_as_of",
                schema: "investments",
                table: "security_breakdown",
                columns: new[] { "isin", "as_of" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "security_breakdown",
                schema: "investments");
        }
    }
}
