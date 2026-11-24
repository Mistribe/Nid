using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Ai.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class _20261124155802_CloudAllowedKinds : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string[]>(
            name: "allowed_kinds",
            schema: "ai",
            table: "ai_settings",
            type: "text[]",
            nullable: false,
            defaultValue: new string[0]);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "allowed_kinds",
            schema: "ai",
            table: "ai_settings");
    }
}
