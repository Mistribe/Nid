using Npgsql;
using Nid.TestSupport;

namespace Nid.Documents.IntegrationTests;

/// <summary>#557: the migrated database has pgvector, and an HNSW index over vectors builds and answers.</summary>
public sealed class VectorExtensionTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task The_migrated_database_has_pgvector_and_an_hnsw_index_answers()
    {
        await using var connection = new NpgsqlConnection(await fixture.NewConnectionStringAsync());
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        Assert.Equal("vector", await ScalarAsync(connection, "select extname from pg_extension where extname = 'vector'"));
        await ScalarAsync(connection, """
            create table vectors (id int primary key, v vector(3));
            insert into vectors values (1, '[1,0,0]'), (2, '[0,1,0]'), (3, '[0,0,1]');
            create index vectors_hnsw on vectors using hnsw (v vector_cosine_ops);
            """);
        Assert.Equal(2, await ScalarAsync(connection, "select id from vectors order by v <=> '[0.1,0.9,0]' limit 1"));
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }
}
