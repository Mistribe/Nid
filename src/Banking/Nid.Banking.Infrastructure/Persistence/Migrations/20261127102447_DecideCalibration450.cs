using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Banking.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class _20261127102447_DecideCalibration450 : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "decide_calibration",
            schema: "banking",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                implementation = table.Column<string>(type: "text", nullable: false),
                version = table.Column<string>(type: "text", nullable: false),
                model = table.Column<string>(type: "text", nullable: false),
                digest = table.Column<string>(type: "text", nullable: false),
                status = table.Column<string>(type: "text", nullable: false),
                code = table.Column<string>(type: "text", nullable: true),
                queued_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                finished_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_decide_calibration", x => x.id);
                table.CheckConstraint("ck_decide_calibration_status", "status in ('queued','running','done','failed')");
            });

        migrationBuilder.CreateTable(
            name: "decide_profile",
            schema: "banking",
            columns: table => new
            {
                implementation = table.Column<string>(type: "text", nullable: false),
                version = table.Column<string>(type: "text", nullable: false),
                model = table.Column<string>(type: "text", nullable: false),
                digest = table.Column<string>(type: "text", nullable: false),
                eval_version = table.Column<int>(type: "integer", nullable: false),
                options = table.Column<int>(type: "integer", nullable: false),
                threshold = table.Column<double>(type: "double precision", nullable: true),
                cold_threshold = table.Column<double>(type: "double precision", nullable: true),
                temperature = table.Column<double>(type: "double precision", nullable: true),
                scoreless = table.Column<bool>(type: "boolean", nullable: false),
                results = table.Column<string>(type: "jsonb", nullable: false),
                measured_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_decide_profile", x => new { x.implementation, x.version, x.model, x.digest });
            });

        migrationBuilder.CreateIndex(
            name: "ix_decide_calibration_implementation_version_model_digest",
            schema: "banking",
            table: "decide_calibration",
            columns: new[] { "implementation", "version", "model", "digest" },
            unique: true,
            filter: "status in ('queued','running')");

        // #450, cold start: a category's option text (« Parent › Name ») embedded per model, written in SQL as pattern_embedding is. A
        // rename changes the text: the indexer drops the row and makes it again.
        migrationBuilder.Sql("""
            create table banking.category_embedding (
                category_id uuid not null references banking.category (id) on delete cascade,
                model text not null,
                text text not null,
                dimension integer not null check (dimension > 0),
                embedding vector not null,
                created_at timestamp with time zone not null,
                constraint pk_category_embedding primary key (category_id, model),
                constraint ck_category_embedding_dimension check (vector_dims(embedding) = dimension)
            );
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("drop table banking.category_embedding;");

        migrationBuilder.DropTable(
            name: "decide_calibration",
            schema: "banking");

        migrationBuilder.DropTable(
            name: "decide_profile",
            schema: "banking");
    }
}
