using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Nid.Banking.Infrastructure.Persistence;

public static class BankingDbContextConfiguration
{
    public static DbContextOptionsBuilder<BankingDbContext> Configure(
        this DbContextOptionsBuilder<BankingDbContext> builder, string connectionString)
    {
        builder.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.UseNodaTime();
            npgsql.MigrationsHistoryTable("__ef_migrations_history", BankingDbContext.Schema);
        });
        return builder;
    }
}

/// <summary>Used by <c>dotnet ef</c> only; the connection string is never opened when adding a migration.</summary>
public sealed class BankingDbContextFactory : IDesignTimeDbContextFactory<BankingDbContext>
{
    public BankingDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<BankingDbContext>().Configure("Host=localhost;Database=nid_design").Options);
}
