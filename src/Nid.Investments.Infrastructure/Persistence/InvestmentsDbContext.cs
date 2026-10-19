using System.Text;
using Microsoft.EntityFrameworkCore;
using Nid.Platform.Infrastructure.Audit;

namespace Nid.Investments.Infrastructure.Persistence;

public sealed class InvestmentsDbContext(DbContextOptions<InvestmentsDbContext> options) : DbContext(options)
{
    public const string Schema = "investments";

    public DbSet<EnvelopeRow> Envelopes => Set<EnvelopeRow>();

    public DbSet<HoldingRow> Holdings => Set<HoldingRow>();

    public DbSet<HoldingSnapshotRow> HoldingSnapshots => Set<HoldingSnapshotRow>();

    public DbSet<HoldingSnapshotLineRow> HoldingSnapshotLines => Set<HoldingSnapshotLineRow>();

    public DbSet<HoldingMovementRow> HoldingMovements => Set<HoldingMovementRow>();

    public DbSet<HoldingCostReportRow> HoldingCostReports => Set<HoldingCostReportRow>();

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
                t.HasCheckConstraint("ck_envelope_uc_share_floor_pct", "uc_share_floor_pct is null or uc_share_floor_pct between 0 and 100");
                t.HasCheckConstraint("ck_envelope_import", "(import_ref is null) = (import_hash is null) and (import_ref is null) = (imported_by is null)");
            });
            e.HasKey(x => x.ItemId);
            e.Property(x => x.ItemId).ValueGeneratedNever();
            e.Property(x => x.ImportHash).HasColumnType("char(64)");
            e.Property(x => x.UcShareFloorPct).HasColumnType("numeric(9,4)");
            // Per importer, never instance-wide (import-format.md §8); no foreign key to core.person across contexts (2B1a §3.2).
            e.HasIndex(x => new { x.ImportedBy, x.ImportRef }).IsUnique().HasFilter("import_ref is not null");
            MapXmin(e.Property(x => x.Version));
        });

        modelBuilder.Entity<HoldingRow>(e =>
        {
            e.ToTable("holding", t =>
            {
                t.HasCheckConstraint("ck_holding_kind", "kind in ('euro_fund','fund','etf','equity','bond','structured','cash','other')");
                t.HasCheckConstraint("ck_holding_asset_class",
                    "asset_class is null or asset_class in ('equity','bond','money_market','mixed','real_estate','capital_guaranteed','other')");
                t.HasCheckConstraint("ck_holding_risk_indicator", "risk_indicator is null or risk_indicator between 1 and 7");
                t.HasCheckConstraint("ck_holding_entry_cost_pct", "entry_cost_pct is null or entry_cost_pct between 0 and 100");
                t.HasCheckConstraint("ck_holding_dates", "closed_on is null or opened_on is null or closed_on >= opened_on");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Isin).HasColumnType("char(12)");
            e.Property(x => x.EntryCostPct).HasColumnType("numeric(9,4)");
            e.HasOne<EnvelopeRow>().WithMany().HasForeignKey(x => x.EnvelopeItemId);
            e.HasIndex(x => new { x.EnvelopeItemId, x.Isin }).IsUnique().HasFilter("isin is not null");
            MapImportMark(e, "holding");
            MapXmin(e.Property(x => x.Version));
        });

        modelBuilder.Entity<HoldingSnapshotRow>(e =>
        {
            e.ToTable("holding_snapshot");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasOne<EnvelopeRow>().WithMany().HasForeignKey(x => x.EnvelopeItemId);
            e.HasIndex(x => new { x.EnvelopeItemId, x.AsOf }).IsUnique();
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.SnapshotId).OnDelete(DeleteBehavior.Cascade);
            MapImportMark(e, "holding_snapshot");
        });

        modelBuilder.Entity<HoldingSnapshotLineRow>(e =>
        {
            e.ToTable("holding_snapshot_line", t =>
                t.HasCheckConstraint("ck_holding_snapshot_line_amounts",
                    "value >= 0 and (units is null or units >= 0) and (unit_price is null or unit_price >= 0) and (cost_basis_per_unit is null or cost_basis_per_unit >= 0)"));
            e.HasKey(x => new { x.SnapshotId, x.HoldingId });
            e.Property(x => x.Units).HasColumnType("numeric(20,6)");
            e.Property(x => x.UnitPrice).HasColumnType("numeric(20,6)");
            e.Property(x => x.CostBasisPerUnit).HasColumnType("numeric(20,6)");
            e.Property(x => x.Value).HasColumnType("numeric(19,4)");
            e.HasOne<HoldingRow>().WithMany().HasForeignKey(x => x.HoldingId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.HoldingId);
        });

        modelBuilder.Entity<HoldingMovementRow>(e =>
        {
            e.ToTable("holding_movement", t =>
            {
                t.HasCheckConstraint("ck_holding_movement_kind",
                    "kind in ('allocation_in','allocation_out','switch_in','switch_out','maturity','fee','interest','income')");
                t.HasCheckConstraint("ck_holding_movement_amounts", "amount > 0 and (unit_price is null or unit_price >= 0)");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Units).HasColumnType("numeric(20,6)");
            e.Property(x => x.UnitPrice).HasColumnType("numeric(20,6)");
            e.Property(x => x.Amount).HasColumnType("numeric(19,4)");
            e.HasOne<HoldingRow>().WithMany().HasForeignKey(x => x.HoldingId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<EnvelopeRow>().WithMany().HasForeignKey(x => x.EnvelopeItemId);
            e.HasIndex(x => new { x.EnvelopeItemId, x.OccurredOn });
            e.HasIndex(x => x.HoldingId);
            MapImportMark(e, "holding_movement");
        });

        modelBuilder.Entity<HoldingCostReportRow>(e =>
        {
            e.ToTable("holding_cost_report", t => t.HasCheckConstraint("ck_holding_cost_report_year", "year between 1900 and 2200"));
            e.HasKey(x => new { x.HoldingId, x.Year });
            foreach (var pct in new[] { nameof(HoldingCostReportRow.GrossReturnPct), nameof(HoldingCostReportRow.TotalFeesPct), nameof(HoldingCostReportRow.NetReturnPct),
                         nameof(HoldingCostReportRow.ContractFeesPct), nameof(HoldingCostReportRow.SupportFeesPct), nameof(HoldingCostReportRow.RetrocessionPct),
                         nameof(HoldingCostReportRow.NetAnnualized5yPct) })
                e.Property(pct).HasColumnType("numeric(9,4)");
            e.HasOne<HoldingRow>().WithMany().HasForeignKey(x => x.HoldingId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<EnvelopeRow>().WithMany().HasForeignKey(x => x.EnvelopeItemId);
            e.HasIndex(x => x.EnvelopeItemId);
        });

        modelBuilder.MapAuditLog(ownedHere: false);
        ApplySnakeCase(modelBuilder);
        // Issue #34 §2.5's name; snake case alone would glue the digit to the word before it.
        modelBuilder.Entity<HoldingCostReportRow>().Property(x => x.NetAnnualized5yPct).HasColumnName("net_annualized_5y_pct");
    }

    /// <summary>import-format.md §8, as the envelope's: the mark is all or nothing, unique per importer, never instance-wide.</summary>
    private static void MapImportMark<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> e, string table) where T : class, IImportMarked
    {
        e.ToTable(t => t.HasCheckConstraint($"ck_{table}_import",
            "(import_ref is null) = (import_hash is null) and (import_ref is null) = (imported_by is null)"));
        e.Property(nameof(IImportMarked.ImportHash)).HasColumnType("char(64)");
        e.HasIndex(nameof(IImportMarked.ImportedBy), nameof(IImportMarked.ImportRef)).IsUnique().HasFilter("import_ref is not null");
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
