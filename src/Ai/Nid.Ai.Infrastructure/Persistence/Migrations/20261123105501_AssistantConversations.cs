using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Ai.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class _20261123105501_AssistantConversations : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "conversation_retention_days",
            schema: "ai",
            table: "ai_settings",
            type: "integer",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "conversation",
            schema: "ai",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                person_id = table.Column<Guid>(type: "uuid", nullable: false),
                title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                provider = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_conversation", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "conversation_message",
            schema: "ai",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                seq = table.Column<int>(type: "integer", nullable: false),
                role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                content = table.Column<string>(type: "text", nullable: false),
                tools = table.Column<string[]>(type: "text[]", nullable: false),
                items = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_conversation_message", x => x.id);
                table.CheckConstraint("ck_conversation_message_role", "role in ('user', 'assistant')");
                table.ForeignKey(
                    name: "fk_conversation_message_conversation_conversation_id",
                    column: x => x.conversation_id,
                    principalSchema: "ai",
                    principalTable: "conversation",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_conversation_person_id_updated_at",
            schema: "ai",
            table: "conversation",
            columns: new[] { "person_id", "updated_at" });

        migrationBuilder.CreateIndex(
            name: "ix_conversation_updated_at",
            schema: "ai",
            table: "conversation",
            column: "updated_at");

        migrationBuilder.CreateIndex(
            name: "ix_conversation_message_conversation_id_seq",
            schema: "ai",
            table: "conversation_message",
            columns: new[] { "conversation_id", "seq" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "conversation_message",
            schema: "ai");

        migrationBuilder.DropTable(
            name: "conversation",
            schema: "ai");

        migrationBuilder.DropColumn(
            name: "conversation_retention_days",
            schema: "ai",
            table: "ai_settings");
    }
}
