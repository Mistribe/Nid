using System.Text;
using Microsoft.EntityFrameworkCore;
using Nid.Platform.Infrastructure.Audit;

namespace Nid.Income.Infrastructure.Persistence;

public sealed class IncomeDbContext(DbContextOptions<IncomeDbContext> options) : DbContext(options)
{
    public const string Schema = "income";

    public DbSet<SourceRow> Sources => Set<SourceRow>();

    public DbSet<PhaseRow> Phases => Set<PhaseRow>();

    public DbSet<OneOffTaxableRow> OneOffTaxables => Set<OneOffTaxableRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<SourceRow>(e =>
        {
            e.ToTable("income_source", t =>
            {
                t.HasCheckConstraint("ck_income_source_kind",
                    "kind in ('salary','self_employment','pension','unemployment','family_allowance','social_benefit','alimony','other')");
                t.HasCheckConstraint("ck_income_source_import", ImportCheck);
            });
            e.HasKey(x => x.ItemId);
            e.Property(x => x.ItemId).ValueGeneratedNever();
            e.Property(x => x.ImportHash).HasColumnType("char(64)");
            // Per importer, never instance-wide (import-format.md §8); no foreign key to core.person across contexts (2B1a §3.2).
            e.HasIndex(x => new { x.ImportedBy, x.ImportRef }).IsUnique().HasFilter("import_ref is not null");
            MapXmin(e.Property(x => x.Version));
        });

        modelBuilder.Entity<PhaseRow>(e =>
        {
            e.ToTable("income_phase", t =>
            {
                t.HasCheckConstraint("ck_income_phase_amounts", "net_amount > 0 and (gross_amount is null or gross_amount > 0)");
                t.HasCheckConstraint("ck_income_phase_every",
                    "(every_unit = 'month' and every_count between 1 and 12) or (every_unit = 'year' and every_count = 1)");
                t.HasCheckConstraint("ck_income_phase_dates", "ends_on is null or ends_on > starts_on");
                t.HasCheckConstraint("ck_income_phase_max_cycles", "max_cycles is null or max_cycles >= 1");
                t.HasCheckConstraint("ck_income_phase_taxable", "taxable_amount is null or taxable_amount > 0");
                t.HasCheckConstraint("ck_income_phase_label", "char_length(label) between 1 and 60");
                t.HasCheckConstraint("ck_income_phase_import", ImportCheck);
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.NetAmount).HasColumnType("numeric(19,4)");
            e.Property(x => x.GrossAmount).HasColumnType("numeric(19,4)");
            e.Property(x => x.TaxableAmount).HasColumnType("numeric(19,4)");
            e.Property(x => x.Currency).HasColumnType("char(3)");
            e.Property(x => x.ImportHash).HasColumnType("char(64)");
            // A source's phases are read by label and start (the overlap rule, spec §2). Overlap itself is checked in code:
            // the database could only express it with an exclusion constraint, which the module does not need.
            e.HasIndex(x => new { x.SourceItemId, x.Label, x.StartsOn });
            e.HasIndex(x => new { x.ImportedBy, x.ImportRef }).IsUnique().HasFilter("import_ref is not null");
            MapXmin(e.Property(x => x.Version));
        });

        modelBuilder.Entity<OneOffTaxableRow>(e =>
        {
            e.ToTable("one_off_taxable", t => t.HasCheckConstraint("ck_one_off_taxable_amount", "taxable_amount > 0"));
            e.HasKey(x => x.FlowId);
            e.Property(x => x.FlowId).ValueGeneratedNever();
            e.Property(x => x.TaxableAmount).HasColumnType("numeric(19,4)");
            // No foreign key to core.flow across contexts (2B1a §3.2): a source's removal takes its rows with it.
            e.HasOne<SourceRow>().WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.MapAuditLog(ownedHere: false);
        ApplySnakeCase(modelBuilder);
    }

    /// <summary>A row is imported or it is not: ref, hash and importer come together (as investments.envelope).</summary>
    private const string ImportCheck = "(import_ref is null) = (import_hash is null) and (import_ref is null) = (imported_by is null)";

    private static void MapXmin(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<uint> property)
        => property.HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

    private static void ApplySnakeCase(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        foreach (var property in entity.GetProperties())
        {
            if (property.Name == nameof(SourceRow.Version))
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
