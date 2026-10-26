using System.Text;
using Microsoft.EntityFrameworkCore;
using Nid.Platform.Infrastructure.Audit;

namespace Nid.Platform.Infrastructure.Persistence;

/// <summary>Import staging (<c>import-format.md</c> §11.3). No business module reads this schema.</summary>
public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    public const string Schema = "platform";

    public DbSet<ImportSessionRow> Sessions => Set<ImportSessionRow>();
    public DbSet<ImportBatchRow> Batches => Set<ImportBatchRow>();
    public DbSet<AuditEntryRow> AuditLog => Set<AuditEntryRow>();
    public DbSet<NotificationRow> Notifications => Set<NotificationRow>();
    public DbSet<NotificationRunRow> NotificationRuns => Set<NotificationRunRow>();

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

        modelBuilder.Entity<NotificationRow>(e =>
        {
            e.ToTable("notification", t =>
            {
                t.HasCheckConstraint("ck_notification_state", "state in ('pending','open','snoozed','dismissed','resolved')");
                t.HasCheckConstraint("ck_notification_level", "level in ('critical','action','info')");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.ItemIds).HasColumnType("uuid[]").HasDefaultValueSql("'{}'");
            e.Property(x => x.Params).HasColumnType("jsonb");
            e.HasIndex(x => new { x.PersonId, x.Key }).IsUnique();
            e.HasIndex(x => new { x.PersonId, x.State });
        });

        modelBuilder.Entity<NotificationRunRow>(e =>
        {
            e.ToTable("notification_run");
            e.HasKey(x => new { x.PersonId, x.Source });
        });

        modelBuilder.MapAuditLog(ownedHere: true);
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
