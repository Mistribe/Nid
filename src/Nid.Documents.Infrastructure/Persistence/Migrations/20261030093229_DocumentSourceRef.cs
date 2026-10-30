using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Documents.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DocumentSourceRef : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "source_ref",
                schema: "documents",
                table: "document",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_document_source_ref",
                schema: "documents",
                table: "document",
                column: "source_ref",
                unique: true,
                filter: "source_ref is not null");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_document_source_ref",
                schema: "documents",
                table: "document");

            migrationBuilder.DropColumn(
                name: "source_ref",
                schema: "documents",
                table: "document");
        }
    }
}
