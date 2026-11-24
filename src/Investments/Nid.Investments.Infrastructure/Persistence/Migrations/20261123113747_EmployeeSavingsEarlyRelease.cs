using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Investments.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class _20261123113747_EmployeeSavingsEarlyRelease : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<decimal>(
            name: "early_release_amount",
            schema: "investments",
            table: "envelope",
            type: "numeric(19,4)",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "early_release_case",
            schema: "investments",
            table: "envelope",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<LocalDate>(
            name: "early_release_on",
            schema: "investments",
            table: "envelope",
            type: "date",
            nullable: true);

        migrationBuilder.AddCheckConstraint(
            name: "ck_envelope_early_release",
            schema: "investments",
            table: "envelope",
            sql: "(early_release_case is null) = (early_release_on is null) and (early_release_case is not null or early_release_amount is null) and (early_release_case is null or kind = 'employee_savings') and (early_release_amount is null or early_release_amount > 0)");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_envelope_early_release",
            schema: "investments",
            table: "envelope");

        migrationBuilder.DropColumn(
            name: "early_release_amount",
            schema: "investments",
            table: "envelope");

        migrationBuilder.DropColumn(
            name: "early_release_case",
            schema: "investments",
            table: "envelope");

        migrationBuilder.DropColumn(
            name: "early_release_on",
            schema: "investments",
            table: "envelope");
    }
}
