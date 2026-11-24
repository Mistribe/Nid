using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace Nid.Documents.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class _20261124074010_FrenchSearch : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterDatabase()
            .Annotation("Npgsql:PostgresExtension:unaccent", ",,")
            .Annotation("Npgsql:PostgresExtension:vector", ",,")
            .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");

        // #582: Nid's French configuration. A word goes through, in order: the stop words, on the word as written (an ispell
        // dictionary checks them on its lemmas, too late); unaccent; Hunspell's French, its accents removed the same way
        // (deploy/postgres); Snowball for the words Hunspell does not know. A server without the dictionary's files (an
        // external one, Neon for the demo) keeps unaccented Snowball: Postgres refuses the dictionary with config_file_error.
        migrationBuilder.Sql("""
            create text search dictionary documents.nid_french_stop (template = pg_catalog.simple, stopwords = french, accept = false);
            create text search configuration documents.nid_french (copy = pg_catalog.french);
            do $$
            begin
                create text search dictionary documents.nid_french_hunspell (template = pg_catalog.ispell, dictfile = nid_fr, afffile = nid_fr);
                alter text search configuration documents.nid_french
                    alter mapping for asciiword, asciihword, hword_asciipart, word, hword, hword_part
                    with documents.nid_french_stop, unaccent, documents.nid_french_hunspell, pg_catalog.french_stem;
            exception when config_file_error then
                alter text search configuration documents.nid_french
                    alter mapping for asciiword, asciihword, hword_asciipart, word, hword, hword_part
                    with documents.nid_french_stop, unaccent, pg_catalog.french_stem;
            end $$;
            """);

        migrationBuilder.AlterColumn<NpgsqlTsVector>(
            name: "search",
            schema: "documents",
            table: "document_chunk",
            type: "tsvector",
            nullable: false,
            oldClrType: typeof(NpgsqlTsVector),
            oldType: "tsvector")
            .Annotation("Npgsql:TsVectorConfig", "documents.nid_french")
            .Annotation("Npgsql:TsVectorProperties", new[] { "text" })
            .OldAnnotation("Npgsql:TsVectorConfig", "french")
            .OldAnnotation("Npgsql:TsVectorProperties", new[] { "text" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<NpgsqlTsVector>(
            name: "search",
            schema: "documents",
            table: "document_chunk",
            type: "tsvector",
            nullable: false,
            oldClrType: typeof(NpgsqlTsVector),
            oldType: "tsvector")
            .Annotation("Npgsql:TsVectorConfig", "french")
            .Annotation("Npgsql:TsVectorProperties", new[] { "text" })
            .OldAnnotation("Npgsql:TsVectorConfig", "documents.nid_french")
            .OldAnnotation("Npgsql:TsVectorProperties", new[] { "text" });

        // Once the column no longer uses the configuration; unaccent goes after, once nothing uses it.
        migrationBuilder.Sql("""
            drop text search configuration documents.nid_french;
            drop text search dictionary if exists documents.nid_french_hunspell;
            drop text search dictionary documents.nid_french_stop;
            """);

        migrationBuilder.AlterDatabase()
            .Annotation("Npgsql:PostgresExtension:vector", ",,")
            .OldAnnotation("Npgsql:PostgresExtension:unaccent", ",,")
            .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");
    }
}
