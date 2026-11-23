using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Ai.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class _20261122183338_AddModelGateway : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "models",
            schema: "ai",
            table: "ai_settings",
            type: "jsonb",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "uses",
            schema: "ai",
            table: "ai_settings",
            type: "jsonb",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "llm_call",
            schema: "ai",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                person_id = table.Column<Guid>(type: "uuid", nullable: true),
                task = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                use = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                worker_id = table.Column<Guid>(type: "uuid", nullable: true),
                conversation_id = table.Column<Guid>(type: "uuid", nullable: true),
                job_id = table.Column<Guid>(type: "uuid", nullable: true),
                model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                tokens_in = table.Column<int>(type: "integer", nullable: false),
                tokens_out = table.Column<int>(type: "integer", nullable: false),
                status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_llm_call", x => x.id);
                table.CheckConstraint("ck_llm_call_status", "status in ('succeeded', 'refused', 'error', 'no_destination', 'blocked_budget')");
            });

        migrationBuilder.CreateIndex(
            name: "ix_llm_call_created_at",
            schema: "ai",
            table: "llm_call",
            column: "created_at");

        migrationBuilder.CreateIndex(
            name: "ix_llm_call_person_id_created_at",
            schema: "ai",
            table: "llm_call",
            columns: new[] { "person_id", "created_at" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "llm_call",
            schema: "ai");

        migrationBuilder.DropColumn(
            name: "models",
            schema: "ai",
            table: "ai_settings");

        migrationBuilder.DropColumn(
            name: "uses",
            schema: "ai",
            table: "ai_settings");
    }
}
