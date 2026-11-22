using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Ai.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class _20261122163505_AddJobListIndex : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "ix_job_created_at_id",
            schema: "ai",
            table: "job",
            columns: new[] { "created_at", "id" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_job_created_at_id",
            schema: "ai",
            table: "job");
    }
}
