using Microsoft.EntityFrameworkCore;
using Nid.Core.Infrastructure.Persistence;
using Nid.Income.Infrastructure.Persistence;
using Nid.Investments.Infrastructure.Persistence;
using Nid.Platform.Infrastructure.Persistence;
using Nid.Subscriptions.Infrastructure.Persistence;
using NodaTime;
using Testcontainers.PostgreSql;

namespace Nid.TestSupport;

/// <summary>One PostgreSQL container for the whole test run; each test gets a database with all three schemas.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    // One database per test and a connection pool per database: the default limit of 100 clients is reached
    // well before the suite is, and the tests that happen to run last fail with "sorry, too many clients
    // already" rather than on their own merits.
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17")
        .WithCommand("-c", "max_connections=500")
        .Build();
    private int databaseCounter;

    public Task InitializeAsync() => container.StartAsync();

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    public async Task<CoreDbContext> NewDatabaseAsync() => NewCoreContext(await NewConnectionStringAsync());

    /// <summary>Creates an empty database, applies every module's migrations, and returns its connection string.</summary>
    public async Task<string> NewConnectionStringAsync()
    {
        var connectionString = await NewEmptyConnectionStringAsync();

        await using (var core = NewCoreContext(connectionString))
            await core.Database.MigrateAsync();
        await using (var subs = NewSubscriptionsContext(connectionString))
            await subs.Database.MigrateAsync();
        await using (var investments = NewInvestmentsContext(connectionString))
            await investments.Database.MigrateAsync();
        await using (var income = NewIncomeContext(connectionString))
            await income.Database.MigrateAsync();
        await using (var platform = NewPlatformContext(connectionString))
            await platform.Database.MigrateAsync();

        return connectionString;
    }

    /// <summary>Creates an empty database with no schema at all, for tests that migrate step by step.</summary>
    public async Task<string> NewEmptyConnectionStringAsync()
    {
        var name = $"nid_test_{Interlocked.Increment(ref databaseCounter)}";
        await using (var admin = new Npgsql.NpgsqlConnection(container.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var command = admin.CreateCommand();
            command.CommandText = $"create database {name}";
            await command.ExecuteNonQueryAsync();
        }

        // One pool per test database, kept by every context that names this string (EF builds its own data source for it):
        // idle connections close after 2 s, or hundreds of finished tests exhaust the server (1F4, 606 tests).
        return new Npgsql.NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Database = name, ConnectionIdleLifetime = 2, ConnectionPruningInterval = 1,
        }.ToString();
    }

    public static CoreDbContext NewCoreContext(string connectionString)
        => new(new DbContextOptionsBuilder<CoreDbContext>().Configure(connectionString).Options);

    public static SubscriptionsDbContext NewSubscriptionsContext(string connectionString)
        => new(new DbContextOptionsBuilder<SubscriptionsDbContext>().Configure(connectionString).Options);

    public static InvestmentsDbContext NewInvestmentsContext(string connectionString)
        => new(new DbContextOptionsBuilder<InvestmentsDbContext>().Configure(connectionString).Options);

    public static IncomeDbContext NewIncomeContext(string connectionString)
        => new(new DbContextOptionsBuilder<IncomeDbContext>().Configure(connectionString).Options);

    public static PlatformDbContext NewPlatformContext(string connectionString)
        => new(new DbContextOptionsBuilder<PlatformDbContext>().Configure(connectionString).Options);

    public static IClock Clock { get; } = SystemClock.Instance;
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
