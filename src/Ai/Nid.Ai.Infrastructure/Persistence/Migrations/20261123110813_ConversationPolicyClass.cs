using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Ai.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class _20261123110813_ConversationPolicyClass : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "policy_class",
            schema: "ai",
            table: "conversation",
            type: "character varying(80)",
            maxLength: 80,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "policy_class",
            schema: "ai",
            table: "conversation");
    }
}
