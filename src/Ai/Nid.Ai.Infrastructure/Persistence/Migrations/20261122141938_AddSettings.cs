using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Ai.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class _20261122141938_AddSettings : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "consent",
            schema: "ai",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                person_id = table.Column<Guid>(type: "uuid", nullable: false),
                subject = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                value = table.Column<bool>(type: "boolean", nullable: false),
                at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_consent", x => x.id);
                table.CheckConstraint("ck_consent_subject", "subject in ('remote_workers')");
            });

        migrationBuilder.CreateTable(
            name: "ai_settings",
            schema: "ai",
            columns: table => new
            {
                id = table.Column<int>(type: "integer", nullable: false),
                remote_workers_allowed = table.Column<bool>(type: "boolean", nullable: false),
                remote_consent_id = table.Column<Guid>(type: "uuid", nullable: true),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_ai_settings", x => x.id);
                table.CheckConstraint("ck_ai_settings_single", "id = 1");
                table.ForeignKey(
                    name: "fk_ai_settings_consent_remote_consent_id",
                    column: x => x.remote_consent_id,
                    principalSchema: "ai",
                    principalTable: "consent",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "ix_ai_settings_remote_consent_id",
            schema: "ai",
            table: "ai_settings",
            column: "remote_consent_id");

        migrationBuilder.CreateIndex(
            name: "ix_consent_subject_at",
            schema: "ai",
            table: "consent",
            columns: new[] { "subject", "at" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ai_settings",
            schema: "ai");

        migrationBuilder.DropTable(
            name: "consent",
            schema: "ai");
    }
}
