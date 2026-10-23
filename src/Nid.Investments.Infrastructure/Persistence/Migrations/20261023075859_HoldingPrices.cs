using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Investments.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HoldingPrices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "auto_price",
                schema: "investments",
                table: "holding",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "price_run",
                schema: "investments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    queried = table.Column<int>(type: "integer", nullable: false),
                    accepted = table.Column<int>(type: "integer", nullable: false),
                    rejected = table.Column<int>(type: "integer", nullable: false),
                    not_found = table.Column<int>(type: "integer", nullable: false),
                    source_errors = table.Column<int>(type: "integer", nullable: false),
                    failure_code = table.Column<string>(type: "text", nullable: true),
                    failure_message = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_price_run", x => x.id);
                    table.CheckConstraint("ck_price_run_status", "status in ('running','succeeded','failed')");
                });

            migrationBuilder.CreateTable(
                name: "security_listing",
                schema: "investments",
                columns: table => new
                {
                    isin = table.Column<string>(type: "char(12)", nullable: false),
                    boursorama_path = table.Column<string>(type: "text", nullable: true),
                    yahoo_symbol = table.Column<string>(type: "text", nullable: true),
                    resolved_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    not_found_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_security_listing", x => x.isin);
                });

            migrationBuilder.CreateTable(
                name: "security_price",
                schema: "investments",
                columns: table => new
                {
                    isin = table.Column<string>(type: "char(12)", nullable: false),
                    on_date = table.Column<LocalDate>(type: "date", nullable: false),
                    price = table.Column<decimal>(type: "numeric(20,6)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    previous = table.Column<decimal>(type: "numeric(20,6)", nullable: true),
                    fetched_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    accepted_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_security_price", x => new { x.isin, x.on_date });
                    table.CheckConstraint("ck_security_price_price", "price > 0");
                    table.CheckConstraint("ck_security_price_status", "status in ('accepted','rejected')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_price_run_started_at",
                schema: "investments",
                table: "price_run",
                column: "started_at");

            migrationBuilder.CreateIndex(
                name: "ix_price_run_status",
                schema: "investments",
                table: "price_run",
                column: "status",
                unique: true,
                filter: "status = 'running'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "price_run",
                schema: "investments");

            migrationBuilder.DropTable(
                name: "security_listing",
                schema: "investments");

            migrationBuilder.DropTable(
                name: "security_price",
                schema: "investments");

            migrationBuilder.DropColumn(
                name: "auto_price",
                schema: "investments",
                table: "holding");
        }
    }
}
