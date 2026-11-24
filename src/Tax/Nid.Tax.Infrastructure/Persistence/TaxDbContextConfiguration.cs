using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Nid.Tax.Infrastructure.Persistence;

public static class TaxDbContextConfiguration
{
    public static DbContextOptionsBuilder<TaxDbContext> Configure(
        this DbContextOptionsBuilder<TaxDbContext> builder, string connectionString)
    {
        builder.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.UseNodaTime();
            npgsql.MigrationsHistoryTable("__ef_migrations_history", TaxDbContext.Schema);
        });
        return builder;
    }
}

/// <summary>Used by <c>dotnet ef</c> only; the connection string is never opened when adding a migration.</summary>
public sealed class TaxDbContextFactory : IDesignTimeDbContextFactory<TaxDbContext>
{
    public TaxDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<TaxDbContext>().Configure("Host=localhost;Database=nid_design").Options);
}
