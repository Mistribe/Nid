using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Ai.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class _20261124153722_JobDispatches : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "job_dispatch",
            schema: "ai",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                job_id = table.Column<Guid>(type: "uuid", nullable: false),
                worker_id = table.Column<Guid>(type: "uuid", nullable: true),
                sent_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                finished_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                outcome = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_job_dispatch", x => x.id);
                table.ForeignKey(
                    name: "fk_job_dispatch_job_job_id",
                    column: x => x.job_id,
                    principalSchema: "ai",
                    principalTable: "job",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_job_dispatch_model_worker_worker_id",
                    column: x => x.worker_id,
                    principalSchema: "ai",
                    principalTable: "model_worker",
                    principalColumn: "id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateIndex(
            name: "ix_job_dispatch_job_id_sent_at",
            schema: "ai",
            table: "job_dispatch",
            columns: new[] { "job_id", "sent_at" });

        migrationBuilder.CreateIndex(
            name: "ix_job_dispatch_worker_id",
            schema: "ai",
            table: "job_dispatch",
            column: "worker_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "job_dispatch",
            schema: "ai");
    }
}
