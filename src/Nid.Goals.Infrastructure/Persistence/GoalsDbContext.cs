using System.Text;
using Microsoft.EntityFrameworkCore;
using Nid.Platform.Infrastructure.Audit;

namespace Nid.Goals.Infrastructure.Persistence;

public sealed class GoalsDbContext(DbContextOptions<GoalsDbContext> options) : DbContext(options)
{
    public const string Schema = "goals";

    public DbSet<GoalRow> Goals => Set<GoalRow>();

    public DbSet<FundingRow> Funding => Set<FundingRow>();

    public DbSet<ProgressEntryRow> Entries => Set<ProgressEntryRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<GoalRow>(e =>
        {
            e.ToTable("goal", t =>
            {
                t.HasCheckConstraint("ck_goal_shape",
                    "(kind = 'savings' and target_amount > 0 and months is null) or "
                    + "(kind = 'emergency_fund' and months between 1 and 24 and target_amount is null)");
                t.HasCheckConstraint("ck_goal_import",
                    "(import_ref is null) = (import_hash is null) and (import_ref is null) = (imported_by is null)");
            });
            e.HasKey(x => x.ItemId);
            e.Property(x => x.ItemId).ValueGeneratedNever();
            e.Property(x => x.TargetAmount).HasColumnType("numeric(19,4)");
            e.Property(x => x.ImportHash).HasColumnType("char(64)");
            // Per importer, never instance-wide (import-format.md §8).
            e.HasIndex(x => new { x.ImportedBy, x.ImportRef }).IsUnique().HasFilter("import_ref is not null");
            MapXmin(e.Property(x => x.Version));
        });

        modelBuilder.Entity<FundingRow>(e =>
        {
            e.ToTable("funding", t => t.HasCheckConstraint("ck_funding_share", "share_bp between 1 and 10000"));
            e.HasKey(x => new { x.GoalId, x.EnvelopeId });
            // The envelope lives in another module's schema: no foreign key across contexts. The reverse lookup is by envelope.
            e.HasIndex(x => x.EnvelopeId);
            e.HasOne<GoalRow>().WithMany().HasForeignKey(x => x.GoalId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProgressEntryRow>(e =>
        {
            e.ToTable("progress_entry", t => t.HasCheckConstraint("ck_progress_entry_amount", "amount >= 0"));
            e.HasKey(x => new { x.GoalId, x.On });
            e.Property(x => x.Amount).HasColumnType("numeric(19,4)");
            e.HasOne<GoalRow>().WithMany().HasForeignKey(x => x.GoalId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.MapAuditLog(ownedHere: false);
        ApplySnakeCase(modelBuilder);
    }

    private static void MapXmin(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<uint> property)
        => property.HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

    private static void ApplySnakeCase(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        foreach (var property in entity.GetProperties())
        {
            if (property.Name == nameof(GoalRow.Version))
                continue; // mapped to the system column xmin

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
