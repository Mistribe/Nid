using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Ai.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class _20261123111406_AssistantPeople : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "assistant_person",
            schema: "ai",
            columns: table => new
            {
                person_id = table.Column<Guid>(type: "uuid", nullable: false),
                enabled = table.Column<bool>(type: "boolean", nullable: false),
                offsite_default = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                changed_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_assistant_person", x => x.person_id);
                table.CheckConstraint("ck_assistant_person_offsite", "offsite_default in ('ask', 'allow', 'never')");
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "assistant_person",
            schema: "ai");
    }
}
