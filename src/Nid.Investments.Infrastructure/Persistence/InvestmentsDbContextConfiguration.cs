using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Nid.Investments.Infrastructure.Persistence;

public static class InvestmentsDbContextConfiguration
{
    public static DbContextOptionsBuilder<InvestmentsDbContext> Configure(
        this DbContextOptionsBuilder<InvestmentsDbContext> builder, string connectionString)
    {
        builder.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.UseNodaTime();
            npgsql.MigrationsHistoryTable("__ef_migrations_history", InvestmentsDbContext.Schema);
        });
        return builder;
    }
}

/// <summary>Used by <c>dotnet ef</c> only; the connection string is never opened when adding a migration.</summary>
public sealed class InvestmentsDbContextFactory : IDesignTimeDbContextFactory<InvestmentsDbContext>
{
    public InvestmentsDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<InvestmentsDbContext>().Configure("Host=localhost;Database=nid_design").Options);
}
