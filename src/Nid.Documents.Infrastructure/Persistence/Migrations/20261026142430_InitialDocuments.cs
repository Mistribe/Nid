using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Nid.Documents.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "documents");

            migrationBuilder.CreateTable(
                name: "document",
                schema: "documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sha256 = table.Column<string>(type: "char(64)", nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    media_type = table.Column<string>(type: "text", nullable: false),
                    original_filename = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    document_date = table.Column<LocalDate>(type: "date", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    uploaded_by_person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    uploaded_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    archived_at = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "document_link",
                schema: "documents",
                columns: table => new
                {
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_kind = table.Column<string>(type: "text", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    anchor_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    role = table.Column<string>(type: "text", nullable: false),
                    linked_by_person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    linked_at = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_link", x => new { x.document_id, x.target_kind, x.target_id });
                    table.CheckConstraint("ck_document_link_role", "role in ('amortization_table', 'deed', 'insurance', 'invoice', 'lease', 'loan_offer', 'other', 'payslip', 'statement', 'tax_notice')");
                    table.ForeignKey(
                        name: "fk_document_link_document_document_id",
                        column: x => x.document_id,
                        principalSchema: "documents",
                        principalTable: "document",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_document_link_anchor_item_id",
                schema: "documents",
                table: "document_link",
                column: "anchor_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_document_link_target_kind_target_id",
                schema: "documents",
                table: "document_link",
                columns: new[] { "target_kind", "target_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_link",
                schema: "documents");

            migrationBuilder.DropTable(
                name: "document",
                schema: "documents");
        }
    }
}
