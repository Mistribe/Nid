using System.Text;
using Microsoft.EntityFrameworkCore;
using Nid.Platform.Infrastructure.Audit;

namespace Nid.Investments.Infrastructure.Persistence;

public sealed class InvestmentsDbContext(DbContextOptions<InvestmentsDbContext> options) : DbContext(options)
{
    public const string Schema = "investments";

    public DbSet<EnvelopeRow> Envelopes => Set<EnvelopeRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<EnvelopeRow>(e =>
        {
            e.ToTable("envelope", t =>
            {
                t.HasCheckConstraint("ck_envelope_kind", "kind in ('checking','savings','pea','life_insurance','per','brokerage','other')");
                t.HasCheckConstraint("ck_envelope_cadence", "valuation_cadence in ('monthly','quarterly','yearly','manual')");
                t.HasCheckConstraint("ck_envelope_account_hint", "account_hint is null or char_length(account_hint) between 1 and 4");
                t.HasCheckConstraint("ck_envelope_stale_after_days", "stale_after_days is null or stale_after_days between 1 and 3650");
                t.HasCheckConstraint("ck_envelope_import", "(import_ref is null) = (import_hash is null) and (import_ref is null) = (imported_by is null)");
            });
            e.HasKey(x => x.ItemId);
            e.Property(x => x.ItemId).ValueGeneratedNever();
            e.Property(x => x.ImportHash).HasColumnType("char(64)");
            // Per importer, never instance-wide (import-format.md §8); no foreign key to core.person across contexts (2B1a §3.2).
            e.HasIndex(x => new { x.ImportedBy, x.ImportRef }).IsUnique().HasFilter("import_ref is not null");
            MapXmin(e.Property(x => x.Version));
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
            if (property.Name == nameof(EnvelopeRow.Version))
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
