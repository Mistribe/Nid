using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace Nid.Documents.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class _20261123163458_DocumentChunks : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "document_chunk",
            schema: "documents",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                document_id = table.Column<Guid>(type: "uuid", nullable: false),
                ordinal = table.Column<int>(type: "integer", nullable: false),
                page = table.Column<int>(type: "integer", nullable: false),
                text = table.Column<string>(type: "text", nullable: false),
                search = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: false)
                    .Annotation("Npgsql:TsVectorConfig", "french")
                    .Annotation("Npgsql:TsVectorProperties", new[] { "text" })
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_document_chunk", x => x.id);
                table.CheckConstraint("ck_document_chunk_page", "page >= 1");
                table.ForeignKey(
                    name: "fk_document_chunk_document_document_id",
                    column: x => x.document_id,
                    principalSchema: "documents",
                    principalTable: "document",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_document_chunk_document_id_ordinal",
            schema: "documents",
            table: "document_chunk",
            columns: new[] { "document_id", "ordinal" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_document_chunk_search",
            schema: "documents",
            table: "document_chunk",
            column: "search")
            .Annotation("Npgsql:IndexMethod", "GIN");

        // #559: a chunk's embeddings, one per model; pgvector's vector of any length (the model's), so out of the EF model and
        // written in SQL (DocumentIndexStore), which also makes each (model, dimension)'s HNSW index.
        migrationBuilder.Sql("""
            create table documents.chunk_embedding (
                chunk_id uuid not null references documents.document_chunk (id) on delete cascade,
                model text not null,
                dimension integer not null check (dimension > 0),
                embedding vector not null,
                created_at timestamp with time zone not null,
                constraint pk_chunk_embedding primary key (chunk_id, model),
                constraint ck_chunk_embedding_dimension check (vector_dims(embedding) = dimension)
            );
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("drop table documents.chunk_embedding;");

        migrationBuilder.DropTable(
            name: "document_chunk",
            schema: "documents");
    }
}
