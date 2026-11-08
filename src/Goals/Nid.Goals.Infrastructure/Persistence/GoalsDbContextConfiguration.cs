using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Nid.Goals.Infrastructure.Persistence;

public static class GoalsDbContextConfiguration
{
    public static DbContextOptionsBuilder<GoalsDbContext> Configure(
        this DbContextOptionsBuilder<GoalsDbContext> builder, string connectionString)
    {
        builder.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.UseNodaTime();
            npgsql.MigrationsHistoryTable("__ef_migrations_history", GoalsDbContext.Schema);
        });
        return builder;
    }
}

/// <summary>Used by <c>dotnet ef</c> only; the connection string is never opened when adding a migration.</summary>
public sealed class GoalsDbContextFactory : IDesignTimeDbContextFactory<GoalsDbContext>
{
    public GoalsDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<GoalsDbContext>().Configure("Host=localhost;Database=nid_design").Options);
}
