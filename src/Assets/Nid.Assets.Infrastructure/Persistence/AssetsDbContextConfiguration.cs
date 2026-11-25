using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Nid.Assets.Infrastructure.Persistence;

public static class AssetsDbContextConfiguration
{
    public static DbContextOptionsBuilder<AssetsDbContext> Configure(
        this DbContextOptionsBuilder<AssetsDbContext> builder, string connectionString)
    {
        builder.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.UseNodaTime();
            npgsql.MigrationsHistoryTable("__ef_migrations_history", AssetsDbContext.Schema);
        });
        return builder;
    }
}

/// <summary>Used by <c>dotnet ef</c> only; the connection string is never opened when adding a migration.</summary>
public sealed class AssetsDbContextFactory : IDesignTimeDbContextFactory<AssetsDbContext>
{
    public AssetsDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<AssetsDbContext>().Configure("Host=localhost;Database=nid_design").Options);
}
