using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Ai.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class _20261122185757_AddBudget : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<decimal>(
            name: "cost_estimate_eur",
            schema: "ai",
            table: "llm_call",
            type: "numeric(12,6)",
            precision: 12,
            scale: 6,
            nullable: true);

        migrationBuilder.AddColumn<decimal>(
            name: "reserved_eur",
            schema: "ai",
            table: "llm_call",
            type: "numeric(12,6)",
            precision: 12,
            scale: 6,
            nullable: true);

        migrationBuilder.AddColumn<decimal>(
            name: "monthly_budget_eur",
            schema: "ai",
            table: "ai_settings",
            type: "numeric(10,2)",
            precision: 10,
            scale: 2,
            nullable: false,
            defaultValue: 0m);

        migrationBuilder.CreateTable(
            name: "budget_month",
            schema: "ai",
            columns: table => new
            {
                month = table.Column<LocalDate>(type: "date", nullable: false),
                spent = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: false),
                reserved = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_budget_month", x => x.month);
                table.CheckConstraint("ck_budget_month_amounts", "spent >= 0 and reserved >= 0");
            });

        migrationBuilder.CreateTable(
            name: "budget_reservation",
            schema: "ai",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                month = table.Column<LocalDate>(type: "date", nullable: false),
                amount = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: false),
                expires_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_budget_reservation", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "ix_budget_reservation_expires_at",
            schema: "ai",
            table: "budget_reservation",
            column: "expires_at");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "budget_month",
            schema: "ai");

        migrationBuilder.DropTable(
            name: "budget_reservation",
            schema: "ai");

        migrationBuilder.DropColumn(
            name: "cost_estimate_eur",
            schema: "ai",
            table: "llm_call");

        migrationBuilder.DropColumn(
            name: "reserved_eur",
            schema: "ai",
            table: "llm_call");

        migrationBuilder.DropColumn(
            name: "monthly_budget_eur",
            schema: "ai",
            table: "ai_settings");
    }
}
