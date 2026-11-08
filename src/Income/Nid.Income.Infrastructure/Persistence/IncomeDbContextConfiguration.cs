using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Nid.Income.Infrastructure.Persistence;

public static class IncomeDbContextConfiguration
{
    public static DbContextOptionsBuilder<IncomeDbContext> Configure(
        this DbContextOptionsBuilder<IncomeDbContext> builder, string connectionString)
    {
        builder.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.UseNodaTime();
            npgsql.MigrationsHistoryTable("__ef_migrations_history", IncomeDbContext.Schema);
        });
        return builder;
    }
}

/// <summary>Used by <c>dotnet ef</c> only; the connection string is never opened when adding a migration.</summary>
public sealed class IncomeDbContextFactory : IDesignTimeDbContextFactory<IncomeDbContext>
{
    public IncomeDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<IncomeDbContext>().Configure("Host=localhost;Database=nid_design").Options);
}
