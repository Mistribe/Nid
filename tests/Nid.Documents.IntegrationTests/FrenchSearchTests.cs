using Microsoft.EntityFrameworkCore;
using Nid.Documents.Infrastructure.Persistence;
using Nid.TestSupport;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Nid.Documents.IntegrationTests;

/// <summary>#582: the chunks' French configuration, without accents and with Hunspell's lemmas in Nid's Postgres image;
/// unaccented Snowball on a server without the dictionary's files.</summary>
public sealed class FrenchSearchTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Config = DocumentsDbContext.FrenchSearch;

    [Theory]
    [InlineData("Avis de taxe foncière", "taxe fonciere")]
    [InlineData("Avis d'impôt sur le revenu", "impots")]
    [InlineData("Quittance de loyer", "loyers")]
    [InlineData("Quittances de loyers", "quittance loyer")]
    [InlineData("Les prélèvements ont été remboursés", "prelevement rembourse")]
    [InlineData("Facture d'électricité", "electricite")]
    public async Task A_search_finds_the_text_whatever_its_accents_and_inflections(string text, string search)
    {
        await using var connection = await OpenAsync(await fixture.NewConnectionStringAsync());

        Assert.True((bool)(await ScalarAsync(connection, $"select to_tsvector('{Config}', $1) @@ websearch_to_tsquery('{Config}', $2)", text, search))!);
    }

    [Fact]
    public async Task Nids_image_has_the_hunspell_dictionary_so_loyer_no_longer_meets_loi_and_stop_words_go()
    {
        await using var connection = await OpenAsync(await fixture.NewConnectionStringAsync());

        Assert.Equal(1L, await ScalarAsync(connection, "select count(*) from pg_ts_dict where dictname = 'nid_french_hunspell'"));
        Assert.False((bool)(await ScalarAsync(connection, $"select to_tsvector('{Config}', 'La loi de finances') @@ websearch_to_tsquery('{Config}', 'loyer')"))!);
        Assert.Equal("'bulletin':2 'salaire':4", await ScalarAsync(connection, $"select to_tsvector('{Config}', 'Le bulletin de salaire à été')::text"));
    }

    [Fact]
    public async Task Without_the_dictionarys_files_the_migration_keeps_unaccented_snowball()
    {
        // pgvector's image alone, as an external server or Neon would be.
        await using var plain = new PostgreSqlBuilder(
                "pgvector/pgvector:0.8.7-pg18-trixie@sha256:ab49524d18df7200b74ac9a475e1db6297fc1eb57da4c214c63d10ecc4b754fa")
            .Build();
        await plain.StartAsync(TestContext.Current.CancellationToken);
        await using (var documents = PostgresFixture.NewDocumentsContext(plain.GetConnectionString()))
            await documents.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await using var connection = await OpenAsync(plain.GetConnectionString());

        Assert.Equal(0L, await ScalarAsync(connection, "select count(*) from pg_ts_dict where dictname = 'nid_french_hunspell'"));
        Assert.Equal("'foncier':2 'tax':1", await ScalarAsync(connection, $"select to_tsvector('{Config}', 'Taxe foncière')::text"));
        Assert.True((bool)(await ScalarAsync(connection, $"select to_tsvector('{Config}', 'Taxe foncière') @@ websearch_to_tsquery('{Config}', 'fonciere')"))!);
    }

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values)
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }
}
