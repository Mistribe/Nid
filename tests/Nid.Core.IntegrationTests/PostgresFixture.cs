using Microsoft.EntityFrameworkCore;
using Nid.Core.Infrastructure.Persistence;
using NodaTime;
using Testcontainers.PostgreSql;

namespace Nid.Core.IntegrationTests;

/// <summary>One PostgreSQL container for the whole test run; each test gets a freshly migrated database.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17").Build();
    private int databaseCounter;

    public Task InitializeAsync() => container.StartAsync();

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    public async Task<CoreDbContext> NewDatabaseAsync() => NewContext(await NewConnectionStringAsync());

    /// <summary>Creates and migrates an empty database and returns its connection string.</summary>
    public async Task<string> NewConnectionStringAsync()
    {
        var name = $"nid_test_{Interlocked.Increment(ref databaseCounter)}";
        await using (var admin = new Npgsql.NpgsqlConnection(container.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var command = admin.CreateCommand();
            command.CommandText = $"create database {name}";
            await command.ExecuteNonQueryAsync();
        }

        var connectionString = new Npgsql.NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Database = name }.ToString();
        await using var db = NewContext(connectionString);
        await db.Database.MigrateAsync();
        return connectionString;
    }

    public static CoreDbContext NewContext(string connectionString)
        => new(new DbContextOptionsBuilder<CoreDbContext>().Configure(connectionString).Options);

    public static IClock Clock { get; } = SystemClock.Instance;
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
