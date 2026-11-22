using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Ai.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class _20261122184138_AddCloudProviders : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "profile_id",
            schema: "ai",
            table: "llm_call",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "provider",
            schema: "ai",
            table: "llm_call",
            type: "character varying(32)",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "tier",
            schema: "ai",
            table: "llm_call",
            type: "character varying(8)",
            maxLength: 8,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "region",
            schema: "ai",
            table: "ai_settings",
            type: "character varying(16)",
            maxLength: 16,
            nullable: true);

        migrationBuilder.CreateTable(
            name: "cloud_profile",
            schema: "ai",
            columns: table => new
            {
                profile_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                accepted_reviewed_on = table.Column<LocalDate>(type: "date", nullable: false),
                enabled_by_person_id = table.Column<Guid>(type: "uuid", nullable: false),
                enabled_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_cloud_profile", x => x.profile_id);
            });

        migrationBuilder.CreateTable(
            name: "llm_provider_credential",
            schema: "ai",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                secret_encrypted = table.Column<byte[]>(type: "bytea", nullable: false),
                hint = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                created_by_person_id = table.Column<Guid>(type: "uuid", nullable: false),
                created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                last_used_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                revoked_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_llm_provider_credential", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "ix_llm_provider_credential_provider",
            schema: "ai",
            table: "llm_provider_credential",
            column: "provider",
            unique: true,
            filter: "revoked_at is null");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "cloud_profile",
            schema: "ai");

        migrationBuilder.DropTable(
            name: "llm_provider_credential",
            schema: "ai");

        migrationBuilder.DropColumn(
            name: "profile_id",
            schema: "ai",
            table: "llm_call");

        migrationBuilder.DropColumn(
            name: "provider",
            schema: "ai",
            table: "llm_call");

        migrationBuilder.DropColumn(
            name: "tier",
            schema: "ai",
            table: "llm_call");

        migrationBuilder.DropColumn(
            name: "region",
            schema: "ai",
            table: "ai_settings");
    }
}
