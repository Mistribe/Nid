using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Ai.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class _20261122122429_AddJobs : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "job",
            schema: "ai",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                version = table.Column<int>(type: "integer", nullable: false),
                input = table.Column<string>(type: "jsonb", nullable: false),
                input_hash = table.Column<string>(type: "char(64)", nullable: false),
                file_ref = table.Column<string>(type: "text", nullable: true),
                data_class = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                target_worker_id = table.Column<Guid>(type: "uuid", nullable: true),
                status = table.Column<string>(type: "text", nullable: false),
                attempts = table.Column<int>(type: "integer", nullable: false),
                run_after = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                next_poll_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                lease_until = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                worker_id = table.Column<Guid>(type: "uuid", nullable: true),
                worker_job_id = table.Column<string>(type: "text", nullable: true),
                response_secret = table.Column<byte[]>(type: "bytea", nullable: true),
                result = table.Column<string>(type: "jsonb", nullable: true),
                error = table.Column<string>(type: "text", nullable: true),
                waiting_reason = table.Column<string>(type: "text", nullable: true),
                created_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                sent_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                finished_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_job", x => x.id);
                table.CheckConstraint("ck_job_attempts", "attempts >= 0");
                table.CheckConstraint("ck_job_status", "status in ('queued', 'running', 'succeeded', 'failed', 'dead')");
                table.ForeignKey(
                    name: "fk_job_model_worker_worker_id",
                    column: x => x.worker_id,
                    principalSchema: "ai",
                    principalTable: "model_worker",
                    principalColumn: "id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateIndex(
            name: "ix_job_status_run_after",
            schema: "ai",
            table: "job",
            columns: new[] { "status", "run_after" });

        migrationBuilder.CreateIndex(
            name: "ix_job_type_input_hash",
            schema: "ai",
            table: "job",
            columns: new[] { "type", "input_hash" },
            unique: true,
            filter: "status in ('queued', 'running')");

        migrationBuilder.CreateIndex(
            name: "ix_job_worker_id",
            schema: "ai",
            table: "job",
            column: "worker_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "job",
            schema: "ai");
    }
}
