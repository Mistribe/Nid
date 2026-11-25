using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nid.Banking.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class PatternEmbeddings450 : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // #450: a suggested pattern's embeddings, one per model; pgvector's vector of any length (the model's), so out of the EF
        // model and written in SQL (PatternIndexStore). A household has a few thousand patterns at most: no index, a scan.
        // The extension is the Documents module's (#559); here only in case this migration runs first.
        migrationBuilder.Sql("""
            create extension if not exists vector;
            create table banking.pattern_embedding (
                pattern text not null,
                model text not null,
                dimension integer not null check (dimension > 0),
                embedding vector not null,
                created_at timestamp with time zone not null,
                constraint pk_pattern_embedding primary key (pattern, model),
                constraint ck_pattern_embedding_dimension check (vector_dims(embedding) = dimension)
            );
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql("drop table banking.pattern_embedding;");
}
