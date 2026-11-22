using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Ai.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class _20261122101731_AddModelWorkers : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "ai");

        migrationBuilder.CreateTable(
            name: "ai_key",
            schema: "ai",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                public_key = table.Column<byte[]>(type: "bytea", nullable: false),
                private_key = table.Column<byte[]>(type: "bytea", nullable: false),
                fingerprint = table.Column<string>(type: "char(64)", nullable: false),
                status = table.Column<string>(type: "text", nullable: false),
                created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_ai_key", x => x.id);
                table.CheckConstraint("ck_ai_key_status", "status in ('active', 'retiring')");
            });

        migrationBuilder.CreateTable(
            name: "model_worker",
            schema: "ai",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                host = table.Column<string>(type: "text", nullable: false),
                port = table.Column<int>(type: "integer", nullable: false),
                proxy_declared = table.Column<bool>(type: "boolean", nullable: false),
                placement = table.Column<string>(type: "text", nullable: false),
                safe = table.Column<bool>(type: "boolean", nullable: false),
                status = table.Column<string>(type: "text", nullable: false),
                worker_public_key = table.Column<byte[]>(type: "bytea", nullable: false),
                worker_key_fingerprint = table.Column<string>(type: "char(64)", nullable: false),
                previous_worker_public_key = table.Column<byte[]>(type: "bytea", nullable: true),
                previous_worker_key_until = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                tls_certificate_sha256 = table.Column<string>(type: "char(64)", nullable: true),
                nid_key_id = table.Column<Guid>(type: "uuid", nullable: true),
                last_declaration = table.Column<string>(type: "jsonb", nullable: true),
                last_seen_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                software_version = table.Column<string>(type: "text", nullable: true),
                created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                paired_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                revoked_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_model_worker", x => x.id);
                table.CheckConstraint("ck_model_worker_placement", "placement in ('local', 'remote')");
                table.CheckConstraint("ck_model_worker_port", "port between 1 and 65535");
                table.CheckConstraint("ck_model_worker_revoked", "(status = 'revoked') = (revoked_at is not null)");
                table.CheckConstraint("ck_model_worker_status", "status in ('pending', 'active', 'disabled', 'revoked')");
                table.ForeignKey(
                    name: "fk_model_worker_ai_key_nid_key_id",
                    column: x => x.nid_key_id,
                    principalSchema: "ai",
                    principalTable: "ai_key",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "ix_ai_key_status",
            schema: "ai",
            table: "ai_key",
            column: "status",
            unique: true,
            filter: "status = 'active'");

        migrationBuilder.CreateIndex(
            name: "ix_model_worker_host_port",
            schema: "ai",
            table: "model_worker",
            columns: new[] { "host", "port" },
            unique: true,
            filter: "status <> 'revoked'");

        migrationBuilder.CreateIndex(
            name: "ix_model_worker_nid_key_id",
            schema: "ai",
            table: "model_worker",
            column: "nid_key_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "model_worker",
            schema: "ai");

        migrationBuilder.DropTable(
            name: "ai_key",
            schema: "ai");
    }
}
