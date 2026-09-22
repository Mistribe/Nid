using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Nid.Subscriptions.Infrastructure.Persistence;

public static class SubscriptionsDbContextConfiguration
{
    public static DbContextOptionsBuilder<SubscriptionsDbContext> Configure(
        this DbContextOptionsBuilder<SubscriptionsDbContext> builder, string connectionString)
    {
        builder.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.UseNodaTime();
            npgsql.MigrationsHistoryTable("__ef_migrations_history", SubscriptionsDbContext.Schema);
        });
        return builder;
    }
}

/// <summary>Used by <c>dotnet ef</c> only; the connection string is never opened when adding a migration.</summary>
public sealed class SubscriptionsDbContextFactory : IDesignTimeDbContextFactory<SubscriptionsDbContext>
{
    public SubscriptionsDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<SubscriptionsDbContext>().Configure("Host=localhost;Database=nid_design").Options);
}
