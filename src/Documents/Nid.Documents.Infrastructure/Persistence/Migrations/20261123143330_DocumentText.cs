using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Documents.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class _20261123143330_DocumentText : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "document_text",
            schema: "documents",
            columns: table => new
            {
                document_id = table.Column<Guid>(type: "uuid", nullable: false),
                page = table.Column<int>(type: "integer", nullable: false),
                text = table.Column<string>(type: "text", nullable: false),
                source = table.Column<string>(type: "text", nullable: false),
                extracted_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_document_text", x => new { x.document_id, x.page });
                table.CheckConstraint("ck_document_text_page", "page >= 1");
                table.CheckConstraint("ck_document_text_source", "source in ('text-layer', 'ocr', 'email', 'none')");
                table.ForeignKey(
                    name: "fk_document_text_document_document_id",
                    column: x => x.document_id,
                    principalSchema: "documents",
                    principalTable: "document",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "document_text_job",
            schema: "documents",
            columns: table => new
            {
                document_id = table.Column<Guid>(type: "uuid", nullable: false),
                job_id = table.Column<Guid>(type: "uuid", nullable: false),
                queued_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_document_text_job", x => x.document_id);
                table.ForeignKey(
                    name: "fk_document_text_job_document_document_id",
                    column: x => x.document_id,
                    principalSchema: "documents",
                    principalTable: "document",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "document_text",
            schema: "documents");

        migrationBuilder.DropTable(
            name: "document_text_job",
            schema: "documents");
    }
}
