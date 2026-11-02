using System.Text;
using Microsoft.EntityFrameworkCore;
using Nid.Platform.Infrastructure.Audit;

namespace Nid.Childcare.Infrastructure.Persistence;

public sealed class ChildcareDbContext(DbContextOptions<ChildcareDbContext> options) : DbContext(options)
{
    public const string Schema = "childcare";

    public DbSet<ArrangementRow> Arrangements => Set<ArrangementRow>();

    public DbSet<TermsRow> Terms => Set<TermsRow>();

    public DbSet<WeekdayRow> Weekdays => Set<WeekdayRow>();

    public DbSet<GapRow> Gaps => Set<GapRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<ArrangementRow>(e =>
        {
            e.ToTable("arrangement", t => t.HasCheckConstraint("ck_arrangement_import",
                "(import_ref is null) = (import_hash is null) and (import_ref is null) = (imported_by is null)"));
            e.HasKey(x => x.ItemId);
            e.Property(x => x.ItemId).ValueGeneratedNever();
            e.Property(x => x.ImportHash).HasColumnType("char(64)");
            // The child lives in Core's schema: no foreign key across contexts.
            e.HasIndex(x => x.ChildId);
            // Per importer, never instance-wide (import-format.md §8).
            e.HasIndex(x => new { x.ImportedBy, x.ImportRef }).IsUnique().HasFilter("import_ref is not null");
            MapXmin(e.Property(x => x.Version));
        });

        modelBuilder.Entity<TermsRow>(e =>
        {
            e.ToTable("terms", t =>
            {
                t.HasCheckConstraint("ck_terms_weeks", "programmed_weeks between 1 and 46");
                t.HasCheckConstraint("ck_terms_dates", "\"to\" is null or \"to\" >= \"from\"");
                t.HasCheckConstraint("ck_terms_amounts",
                    "net_hourly_rate > 0 and overtime_rate >= 0 and upkeep_per9h >= 0 and (meal_price is null or meal_price >= 0) and (gross_hourly_rate is null or gross_hourly_rate > 0)");
            });
            e.HasKey(x => new { x.ItemId, x.From });
            e.Property(x => x.NetHourlyRate).HasColumnType("numeric(19,4)");
            e.Property(x => x.GrossHourlyRate).HasColumnType("numeric(19,4)");
            e.Property(x => x.OvertimeRate).HasColumnType("numeric(9,4)");
            e.Property(x => x.UpkeepPer9h).HasColumnType("numeric(19,4)");
            e.Property(x => x.MealPrice).HasColumnType("numeric(19,4)");
            e.HasOne<ArrangementRow>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WeekdayRow>(e =>
        {
            e.ToTable("weekday", t =>
            {
                t.HasCheckConstraint("ck_weekday_day", "day between 1 and 7");
                t.HasCheckConstraint("ck_weekday_hours", "departure > arrival");
            });
            e.HasKey(x => new { x.ItemId, x.TermsFrom, x.Day });
            e.HasOne<TermsRow>().WithMany().HasForeignKey(x => new { x.ItemId, x.TermsFrom }).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GapRow>(e =>
        {
            e.ToTable("gap", t => t.HasCheckConstraint("ck_gap_weeks", "weeks >= 1"));
            e.HasKey(x => new { x.ItemId, x.Monday });
            e.HasOne<ArrangementRow>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
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
            if (property.Name == nameof(ArrangementRow.Version))
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
