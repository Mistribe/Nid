using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Nid.Platform.Infrastructure.Persistence;

/// <summary>Import staging (<c>import-format.md</c> §11.3). No business module reads this schema.</summary>
public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    public const string Schema = "platform";

    public DbSet<ImportSessionRow> Sessions => Set<ImportSessionRow>();
    public DbSet<ImportBatchRow> Batches => Set<ImportBatchRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<ImportSessionRow>(e =>
        {
            e.ToTable("import_session", t => t.HasCheckConstraint("ck_import_session_state",
                "state in ('open','validated','invalid','committed','cancelled','expired')"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Adults).HasColumnType("jsonb");
            e.Property(x => x.Report).HasColumnType("jsonb");
            // One active session per person (§11.5), enforced by the database so two racing starts cannot both win.
            e.HasIndex(x => x.PersonId).IsUnique().HasFilter("state in ('open','validated','invalid')");
            e.HasMany<ImportBatchRow>().WithOne().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ImportBatchRow>(e =>
        {
            e.ToTable("import_batch", t =>
            {
                t.HasCheckConstraint("ck_import_batch_index", "batch_index >= 0");
                t.HasCheckConstraint("ck_import_batch_count", "entity_count >= 0");
            });
            e.HasKey(x => new { x.SessionId, x.Section, x.BatchIndex });
            e.Property(x => x.ContentHash).HasColumnType("char(64)");
            e.Property(x => x.Payload).HasColumnType("jsonb");
        });

        ApplySnakeCase(modelBuilder);
    }

    private static void ApplySnakeCase(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        foreach (var property in entity.GetProperties())
        {
            property.SetColumnName(ToSnakeCase(property.Name));
        }

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            var table = entity.GetTableName()!;
            entity.FindPrimaryKey()?.SetName($"pk_{table}");

            foreach (var index in entity.GetIndexes())
                index.SetDatabaseName($"ix_{table}_{string.Join('_', index.Properties.Select(p => p.GetColumnName()))}");

            foreach (var foreignKey in entity.GetForeignKeys())
                foreignKey.SetConstraintName(
                    $"fk_{table}_{foreignKey.PrincipalEntityType.GetTableName()}_{string.Join('_', foreignKey.Properties.Select(p => p.GetColumnName()))}");
        }
    }

    internal static string ToSnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
                builder.Append('_');
            builder.Append(char.ToLowerInvariant(name[i]));
        }

        return builder.ToString();
    }
}
