using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FxRates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "fx_rate",
                schema: "core",
                columns: table => new
                {
                    on_date = table.Column<LocalDate>(type: "date", nullable: false),
                    quote = table.Column<string>(type: "char(3)", nullable: false),
                    rate = table.Column<decimal>(type: "numeric(19,10)", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    fetched_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fx_rate", x => new { x.on_date, x.quote });
                    table.CheckConstraint("ck_fx_rate_not_usd", "quote <> 'USD'");
                    table.CheckConstraint("ck_fx_rate_positive", "rate > 0");
                    table.CheckConstraint("ck_fx_rate_source", "source in ('ecb','subtracker_data','manual')");
                });

            migrationBuilder.CreateTable(
                name: "fx_sync_run",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    files_read = table.Column<int>(type: "integer", nullable: false),
                    rows_written = table.Column<int>(type: "integer", nullable: false),
                    failure_code = table.Column<string>(type: "text", nullable: true),
                    failure_message = table.Column<string>(type: "text", nullable: true),
                    rejected = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fx_sync_run", x => x.id);
                    table.CheckConstraint("ck_fx_sync_run_kind", "kind in ('daily','backfill')");
                    table.CheckConstraint("ck_fx_sync_run_status", "status in ('running','succeeded','failed')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_fx_sync_run_started_at",
                schema: "core",
                table: "fx_sync_run",
                column: "started_at");

            migrationBuilder.CreateIndex(
                name: "ix_fx_sync_run_status",
                schema: "core",
                table: "fx_sync_run",
                column: "status",
                unique: true,
                filter: "status = 'running'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fx_rate",
                schema: "core");

            migrationBuilder.DropTable(
                name: "fx_sync_run",
                schema: "core");
        }
    }
}
