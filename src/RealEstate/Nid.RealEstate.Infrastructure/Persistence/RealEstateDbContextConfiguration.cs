using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Nid.RealEstate.Infrastructure.Persistence;

public static class RealEstateDbContextConfiguration
{
    public static DbContextOptionsBuilder<RealEstateDbContext> Configure(
        this DbContextOptionsBuilder<RealEstateDbContext> builder, string connectionString)
    {
        builder.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.UseNodaTime();
            npgsql.MigrationsHistoryTable("__ef_migrations_history", RealEstateDbContext.Schema);
        });
        return builder;
    }
}

/// <summary>Used by <c>dotnet ef</c> only; the connection string is never opened when adding a migration.</summary>
public sealed class RealEstateDbContextFactory : IDesignTimeDbContextFactory<RealEstateDbContext>
{
    public RealEstateDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<RealEstateDbContext>().Configure("Host=localhost;Database=nid_design").Options);
}
