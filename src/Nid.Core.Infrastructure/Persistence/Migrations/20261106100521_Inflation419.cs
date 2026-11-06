using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Inflation419 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "inflation_enabled",
                schema: "core",
                table: "household_settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "cpi",
                schema: "core",
                columns: table => new
                {
                    month = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<decimal>(type: "numeric(10,4)", nullable: false),
                    published_on = table.Column<LocalDate>(type: "date", nullable: true),
                    fetched_on = table.Column<LocalDate>(type: "date", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cpi", x => x.month);
                    table.CheckConstraint("ck_cpi_month", "month ~ '^[0-9]{4}-(0[1-9]|1[0-2])$'");
                    table.CheckConstraint("ck_cpi_source", "source in ('insee','manual')");
                    table.CheckConstraint("ck_cpi_value", "value > 0");
                });

            migrationBuilder.CreateTable(
                name: "cpi_run",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    added = table.Column<int>(type: "integer", nullable: false),
                    replaced = table.Column<int>(type: "integer", nullable: false),
                    rejected = table.Column<int>(type: "integer", nullable: false),
                    message = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cpi_run", x => x.id);
                    table.CheckConstraint("ck_cpi_run_status", "status in ('running','succeeded','failed')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_cpi_run_started_at",
                schema: "core",
                table: "cpi_run",
                column: "started_at");

            migrationBuilder.CreateIndex(
                name: "ix_cpi_run_status",
                schema: "core",
                table: "cpi_run",
                column: "status",
                unique: true,
                filter: "status = 'running'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cpi",
                schema: "core");

            migrationBuilder.DropTable(
                name: "cpi_run",
                schema: "core");

            migrationBuilder.DropColumn(
                name: "inflation_enabled",
                schema: "core",
                table: "household_settings");
        }
    }
}
