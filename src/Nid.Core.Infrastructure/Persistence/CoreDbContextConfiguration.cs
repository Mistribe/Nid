using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Nid.Core.Infrastructure.Persistence;

public static class CoreDbContextConfiguration
{
    public static DbContextOptionsBuilder<CoreDbContext> Configure(
        this DbContextOptionsBuilder<CoreDbContext> builder, string connectionString)
    {
        builder.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.UseNodaTime();
            npgsql.MigrationsHistoryTable("__ef_migrations_history", CoreDbContext.Schema);
        });
        return builder;
    }
}

/// <summary>Used by <c>dotnet ef</c> only; the connection string is never opened when adding a migration.</summary>
public sealed class CoreDbContextFactory : IDesignTimeDbContextFactory<CoreDbContext>
{
    public CoreDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<CoreDbContext>().Configure("Host=localhost;Database=nid_design").Options);
}
