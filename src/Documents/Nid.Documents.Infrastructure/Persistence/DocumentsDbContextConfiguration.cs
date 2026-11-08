using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Nid.Documents.Infrastructure.Persistence;

public static class DocumentsDbContextConfiguration
{
    public static DbContextOptionsBuilder<DocumentsDbContext> Configure(
        this DbContextOptionsBuilder<DocumentsDbContext> builder, string connectionString)
    {
        builder.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.UseNodaTime();
            npgsql.MigrationsHistoryTable("__ef_migrations_history", DocumentsDbContext.Schema);
        });
        return builder;
    }
}

/// <summary>Used by <c>dotnet ef</c> only; the connection string is never opened when adding a migration.</summary>
public sealed class DocumentsDbContextFactory : IDesignTimeDbContextFactory<DocumentsDbContext>
{
    public DocumentsDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<DocumentsDbContext>().Configure("Host=localhost;Database=nid_design").Options);
}
