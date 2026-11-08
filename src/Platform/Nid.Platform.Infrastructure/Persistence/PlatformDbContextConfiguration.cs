using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Nid.Platform.Infrastructure.Persistence;

public static class PlatformDbContextConfiguration
{
    public static DbContextOptionsBuilder<PlatformDbContext> Configure(
        this DbContextOptionsBuilder<PlatformDbContext> builder, string connectionString)
    {
        builder.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.UseNodaTime();
            npgsql.MigrationsHistoryTable("__ef_migrations_history", PlatformDbContext.Schema);
        });
        return builder;
    }
}

/// <summary>Used by <c>dotnet ef</c> only; the connection string is never opened when adding a migration.</summary>
public sealed class PlatformDbContextFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    public PlatformDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<PlatformDbContext>().Configure("Host=localhost;Database=nid_design").Options);
}
