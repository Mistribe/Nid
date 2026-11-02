using Microsoft.EntityFrameworkCore;
using Nid.Banking.Infrastructure.Persistence;
using Nid.Childcare.Infrastructure.Persistence;
using Nid.Core.Infrastructure.Persistence;
using Nid.Documents.Infrastructure.Persistence;
using Nid.Goals.Infrastructure.Persistence;
using Nid.Income.Infrastructure.Persistence;
using Nid.Investments.Infrastructure.Persistence;
using Nid.Platform.Infrastructure.Persistence;
using Nid.RealEstate.Infrastructure.Persistence;
using Nid.Subscriptions.Infrastructure.Persistence;
using NodaTime;
using Testcontainers.PostgreSql;

namespace Nid.TestSupport;

/// <summary>
/// One PostgreSQL container for the whole test run; each test gets a database with all three schemas. The container is
/// the process's, not the fixture's: test classes each holding a fixture run in parallel against it.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    // One database per test and a connection pool per database: the default limit of 100 clients is reached
    // well before the suite is, and the tests that happen to run last fail with "sorry, too many clients
    // already" rather than on their own merits.
    private static readonly PostgreSqlContainer Container = new PostgreSqlBuilder("postgres:17")
        .WithCommand("-c", "max_connections=500")
        .Build();
    private static readonly Lazy<Task> Started = new(() => Container.StartAsync());
    private static int databaseCounter;

    public ValueTask InitializeAsync() => new(Started.Value);

    // Testcontainers' reaper removes the container when the test process ends.
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public async Task<CoreDbContext> NewDatabaseAsync() => NewCoreContext(await NewConnectionStringAsync());

    /// <summary>Creates a database with every module's migrations applied, and returns its connection string.</summary>
    /// <remarks>A copy of a template migrated once for the process: nine migrations per test cost more than the copy.</remarks>
    public async Task<string> NewConnectionStringAsync()
    {
        await Template.Value;
        return await NewDatabaseAsync(TemplateName);
    }

    /// <summary>Creates an empty database with no schema at all, for tests that migrate step by step.</summary>
    public Task<string> NewEmptyConnectionStringAsync() => NewDatabaseAsync(template: null);

    private const string TemplateName = "nid_template";
    private static readonly Lazy<Task> Template = new(MigrateTemplateAsync);

    private static async Task MigrateTemplateAsync()
    {
        await Started.Value;
        await ExecuteAsync($"create database {TemplateName}");
        // No pooling: Postgres copies a template only while no session is connected to it.
        await MigrateAsync(new Npgsql.NpgsqlConnectionStringBuilder(Container.GetConnectionString())
        {
            Database = TemplateName, Pooling = false,
        }.ToString());
    }

    private static async Task MigrateAsync(string connectionString)
    {
        await using (var core = NewCoreContext(connectionString))
            await core.Database.MigrateAsync();
        await using (var subs = NewSubscriptionsContext(connectionString))
            await subs.Database.MigrateAsync();
        await using (var investments = NewInvestmentsContext(connectionString))
            await investments.Database.MigrateAsync();
        await using (var income = NewIncomeContext(connectionString))
            await income.Database.MigrateAsync();
        await using (var realEstate = NewRealEstateContext(connectionString))
            await realEstate.Database.MigrateAsync();
        await using (var platform = NewPlatformContext(connectionString))
            await platform.Database.MigrateAsync();
        await using (var goals = NewGoalsContext(connectionString))
            await goals.Database.MigrateAsync();
        await using (var documents = NewDocumentsContext(connectionString))
            await documents.Database.MigrateAsync();
        await using (var banking = NewBankingContext(connectionString))
            await banking.Database.MigrateAsync();
        await using (var childcare = NewChildcareContext(connectionString))
            await childcare.Database.MigrateAsync();
    }

    private static async Task<string> NewDatabaseAsync(string? template)
    {
        var name = $"nid_test_{Interlocked.Increment(ref databaseCounter)}";
        await ExecuteAsync(template is null ? $"create database {name}" : $"create database {name} template {template}");

        // One pool per test database, kept by every context that names this string (EF builds its own data source for it):
        // idle connections close after 2 s, or hundreds of finished tests exhaust the server (1F4, 606 tests).
        return new Npgsql.NpgsqlConnectionStringBuilder(Container.GetConnectionString())
        {
            Database = name, ConnectionIdleLifetime = 2, ConnectionPruningInterval = 1,
        }.ToString();
    }

    private static async Task ExecuteAsync(string sql)
    {
        await using var admin = new Npgsql.NpgsqlConnection(Container.GetConnectionString());
        await admin.OpenAsync();
        await using var command = admin.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public static CoreDbContext NewCoreContext(string connectionString)
        => new(new DbContextOptionsBuilder<CoreDbContext>().Configure(connectionString).Options);

    public static SubscriptionsDbContext NewSubscriptionsContext(string connectionString)
        => new(new DbContextOptionsBuilder<SubscriptionsDbContext>().Configure(connectionString).Options);

    public static InvestmentsDbContext NewInvestmentsContext(string connectionString)
        => new(new DbContextOptionsBuilder<InvestmentsDbContext>().Configure(connectionString).Options);

    public static IncomeDbContext NewIncomeContext(string connectionString)
        => new(new DbContextOptionsBuilder<IncomeDbContext>().Configure(connectionString).Options);

    public static RealEstateDbContext NewRealEstateContext(string connectionString)
        => new(new DbContextOptionsBuilder<RealEstateDbContext>().Configure(connectionString).Options);

    public static PlatformDbContext NewPlatformContext(string connectionString)
        => new(new DbContextOptionsBuilder<PlatformDbContext>().Configure(connectionString).Options);

    public static GoalsDbContext NewGoalsContext(string connectionString)
        => new(new DbContextOptionsBuilder<GoalsDbContext>().Configure(connectionString).Options);

    public static DocumentsDbContext NewDocumentsContext(string connectionString)
        => new(new DbContextOptionsBuilder<DocumentsDbContext>().Configure(connectionString).Options);

    public static BankingDbContext NewBankingContext(string connectionString)
        => new(new DbContextOptionsBuilder<BankingDbContext>().Configure(connectionString).Options);

    public static ChildcareDbContext NewChildcareContext(string connectionString)
        => new(new DbContextOptionsBuilder<ChildcareDbContext>().Configure(connectionString).Options);

    public static IClock Clock { get; } = SystemClock.Instance;
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
