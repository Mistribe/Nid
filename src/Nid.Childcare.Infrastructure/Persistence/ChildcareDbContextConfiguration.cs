using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Nid.Childcare.Infrastructure.Persistence;

public static class ChildcareDbContextConfiguration
{
    public static DbContextOptionsBuilder<ChildcareDbContext> Configure(
        this DbContextOptionsBuilder<ChildcareDbContext> builder, string connectionString)
    {
        builder.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.UseNodaTime();
            npgsql.MigrationsHistoryTable("__ef_migrations_history", ChildcareDbContext.Schema);
        });
        return builder;
    }
}

/// <summary>Used by <c>dotnet ef</c> only; the connection string is never opened when adding a migration.</summary>
public sealed class ChildcareDbContextFactory : IDesignTimeDbContextFactory<ChildcareDbContext>
{
    public ChildcareDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<ChildcareDbContext>().Configure("Host=localhost;Database=nid_design").Options);
}
