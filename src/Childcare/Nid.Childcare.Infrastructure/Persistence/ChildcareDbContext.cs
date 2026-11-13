using System.Text;
using Microsoft.EntityFrameworkCore;
using Nid.Platform.Infrastructure.Audit;

namespace Nid.Childcare.Infrastructure.Persistence;

public sealed class ChildcareDbContext(DbContextOptions<ChildcareDbContext> options) : DbContext(options)
{
    public const string Schema = "childcare";

    public DbSet<ArrangementRow> Arrangements => Set<ArrangementRow>();

    public DbSet<TermsRow> Terms => Set<TermsRow>();

    public DbSet<WeekTypeRow> WeekTypes => Set<WeekTypeRow>();

    public DbSet<WeekdayRow> Weekdays => Set<WeekdayRow>();

    public DbSet<WeekOverrideRow> WeekOverrides => Set<WeekOverrideRow>();

    public DbSet<GapRow> Gaps => Set<GapRow>();

    public DbSet<StatementRow> Statements => Set<StatementRow>();

    public DbSet<StatementDayRow> StatementDays => Set<StatementDayRow>();

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
                t.HasCheckConstraint("ck_terms_dates", "\"to\" is null or \"to\" >= \"from\"");
                t.HasCheckConstraint("ck_terms_amounts",
                    "net_hourly_rate > 0 and overtime_rate >= 0 and upkeep_per9h >= 0 and (lunch_price is null or lunch_price >= 0) and (snack_price is null or snack_price >= 0) and (gross_hourly_rate is null or gross_hourly_rate > 0)");
                t.HasCheckConstraint("ck_terms_meal_providers", "lunch_provider in ('nanny', 'employer') and snack_provider in ('nanny', 'employer')");
            });
            e.HasKey(x => new { x.ItemId, x.From });
            e.Property(x => x.NetHourlyRate).HasColumnType("numeric(19,4)");
            e.Property(x => x.GrossHourlyRate).HasColumnType("numeric(19,4)");
            e.Property(x => x.OvertimeRate).HasColumnType("numeric(9,4)");
            e.Property(x => x.UpkeepPer9h).HasColumnType("numeric(19,4)");
            e.Property(x => x.LunchPrice).HasColumnType("numeric(19,4)");
            e.Property(x => x.SnackPrice).HasColumnType("numeric(19,4)");
            e.Property(x => x.LunchProvider).HasMaxLength(8).HasDefaultValue("nanny");
            e.Property(x => x.SnackProvider).HasMaxLength(8).HasDefaultValue("nanny");
            e.Property(x => x.UnpaidAbsences).HasColumnType("text[]").HasDefaultValueSql("'{parent_rtt,child_sick,carer_absent}'");
            e.HasOne<ArrangementRow>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WeekTypeRow>(e =>
        {
            e.ToTable("week_type", t => t.HasCheckConstraint("ck_week_type_weeks", "weeks >= 1"));
            e.HasKey(x => new { x.ItemId, x.TermsFrom, x.Rank });
            e.Property(x => x.Name).HasMaxLength(40);
            e.HasOne<TermsRow>().WithMany().HasForeignKey(x => new { x.ItemId, x.TermsFrom }).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WeekdayRow>(e =>
        {
            e.ToTable("weekday", t =>
            {
                t.HasCheckConstraint("ck_weekday_day", "day between 1 and 7");
                t.HasCheckConstraint("ck_weekday_hours", "departure > arrival");
            });
            e.HasKey(x => new { x.ItemId, x.TermsFrom, x.WeekType, x.Day });
            e.HasOne<WeekTypeRow>().WithMany().HasForeignKey(x => new { x.ItemId, x.TermsFrom, x.WeekType }).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WeekOverrideRow>(e =>
        {
            e.ToTable("week_override");
            e.HasKey(x => new { x.ItemId, x.Monday });
            e.HasOne<ArrangementRow>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GapRow>(e =>
        {
            e.ToTable("gap", t => t.HasCheckConstraint("ck_gap_weeks", "weeks >= 1"));
            e.HasKey(x => new { x.ItemId, x.Monday });
            e.HasOne<ArrangementRow>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StatementRow>(e =>
        {
            e.ToTable("statement", t =>
            {
                t.HasCheckConstraint("ck_statement_month", "extract(day from month) = 1");
                t.HasCheckConstraint("ck_statement_amounts", "mileage >= 0 and (cmg is null or cmg >= 0) and (contributions is null or contributions >= 0)");
                // Decision 14: a validated month has its figures frozen, a draft has none.
                t.HasCheckConstraint("ck_statement_figures",
                    "validated = (validated_at is not null) and validated = (net_pay is not null) and validated = (cost is not null) and validated = (child_sick_deducted is not null)");
            });
            e.HasKey(x => new { x.ItemId, x.Month });
            foreach (var amount in new[] { nameof(StatementRow.Mileage), nameof(StatementRow.Cmg), nameof(StatementRow.Contributions), nameof(StatementRow.DeclaredNetPay), nameof(StatementRow.DeclaredUpkeep), nameof(StatementRow.DeclaredMeals), nameof(StatementRow.NetPay), nameof(StatementRow.Upkeep),
                         nameof(StatementRow.Meals), nameof(StatementRow.BasePay), nameof(StatementRow.Deduction), nameof(StatementRow.ExtraPay),
                         nameof(StatementRow.PaidLeavePay), nameof(StatementRow.Cost), nameof(StatementRow.TaxBase) })
                e.Property(amount).HasColumnType("numeric(19,4)");
            e.Property(x => x.ComplementaryHours).HasColumnType("numeric(9,2)");
            e.Property(x => x.OvertimeHours).HasColumnType("numeric(9,2)");
            e.Property(x => x.DeclaredComplementaryHours).HasColumnType("numeric(9,2)");
            e.Property(x => x.DeclaredOvertimeHours).HasColumnType("numeric(9,2)");
            e.HasOne<ArrangementRow>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
            MapXmin(e.Property(x => x.Version));
        });

        modelBuilder.Entity<StatementDayRow>(e =>
        {
            e.ToTable("statement_day", t =>
            {
                t.HasCheckConstraint("ck_statement_day_absence", "absence is null or absence in ('family', 'parent_rtt', 'child_sick', 'carer_absent', 'holiday', 'paid_leave', 'carer_training', 'family_agreed')");
                t.HasCheckConstraint("ck_statement_day_hours", "not present or (arrival is not null and departure > arrival)");
            });
            e.HasKey(x => new { x.ItemId, x.Date });
            e.HasOne<StatementRow>().WithMany().HasForeignKey(x => new { x.ItemId, x.Month }).OnDelete(DeleteBehavior.Cascade);
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
