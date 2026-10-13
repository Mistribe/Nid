using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nid.Platform.Infrastructure.Audit;

namespace Nid.RealEstate.Infrastructure.Persistence;

public sealed class RealEstateDbContext(DbContextOptions<RealEstateDbContext> options) : DbContext(options)
{
    public const string Schema = "realestate";

    public DbSet<LoanRow> Loans => Set<LoanRow>();

    public DbSet<LoanPhaseRow> Phases => Set<LoanPhaseRow>();

    public DbSet<LoanInsuranceRow> Insurances => Set<LoanInsuranceRow>();

    public DbSet<LoanFeeRow> Fees => Set<LoanFeeRow>();

    public DbSet<LoanEventRow> Events => Set<LoanEventRow>();

    public DbSet<PropertyRow> Properties => Set<PropertyRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<LoanRow>(e =>
        {
            e.ToTable("loan", t =>
            {
                t.HasCheckConstraint("ck_loan_principal", "principal > 0");
                t.HasCheckConstraint("ck_loan_dates", "first_payment_date >= disbursed_on");
                t.HasCheckConstraint("ck_loan_total_payments", "total_payments between 1 and 600");
                t.HasCheckConstraint("ck_loan_rounding_mode", "rounding_mode in ('half_up','truncate')");
                t.HasCheckConstraint("ck_loan_first_period_mode", "first_period_mode in ('full','prorata_actual_365')");
            });
            e.HasKey(x => x.ItemId);
            e.Property(x => x.ItemId).ValueGeneratedNever();
            e.Property(x => x.Principal).HasColumnType("numeric(19,4)");
            e.HasIndex(x => x.PropertyItemId);
            MapXmin(e.Property(x => x.Version));
        });

        modelBuilder.Entity<LoanPhaseRow>(e =>
        {
            e.ToTable("loan_phase", t =>
            {
                t.HasCheckConstraint("ck_loan_phase_mode", "mode in ('annuity','deferred_partial','deferred_total')");
                t.HasCheckConstraint("ck_loan_phase_rate_type", "rate_type in ('fixed','variable')");
                t.HasCheckConstraint("ck_loan_phase_annual_rate", "annual_rate >= 0 and annual_rate < 1");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.AnnualRate).HasColumnType("numeric(9,6)");
            e.Property(x => x.PaymentOverride).HasColumnType("numeric(19,4)");
            e.Property(x => x.OpeningBalanceOverride).HasColumnType("numeric(19,4)");
            e.HasIndex(x => new { x.LoanItemId, x.Seq }).IsUnique();
        });

        modelBuilder.Entity<LoanInsuranceRow>(e =>
        {
            e.ToTable("loan_insurance", t =>
                t.HasCheckConstraint("ck_loan_insurance_mode", "mode in ('fixed_monthly','rate_on_initial','rate_on_remaining')"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.MonthlyAmount).HasColumnType("numeric(19,4)");
            e.Property(x => x.AnnualRate).HasColumnType("numeric(9,6)");
            e.HasIndex(x => x.LoanItemId);
        });

        modelBuilder.Entity<LoanFeeRow>(e =>
        {
            e.ToTable("loan_fee", t =>
            {
                t.HasCheckConstraint("ck_loan_fee_kind", "kind in ('file','guarantee','early_repayment_penalty','renegotiation','other')");
                t.HasCheckConstraint("ck_loan_fee_amount", "amount > 0");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Amount).HasColumnType("numeric(19,4)");
            e.HasIndex(x => x.LoanItemId);
            MapXmin(e.Property(x => x.Version));
        });

        modelBuilder.Entity<LoanEventRow>(e =>
        {
            e.ToTable("loan_event", t =>
            {
                t.HasCheckConstraint("ck_loan_event_effect", "effect in ('reduce_term','reduce_payment')");
                t.HasCheckConstraint("ck_loan_event_amount", "amount > 0");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Amount).HasColumnType("numeric(19,4)");
            e.HasIndex(x => x.LoanItemId);
            MapXmin(e.Property(x => x.Version));
        });

        modelBuilder.Entity<PropertyRow>(e =>
        {
            e.ToTable("property", t =>
            {
                t.HasCheckConstraint("ck_property_type",
                    "property_type in ('primary_residence','secondary_residence','rental','parking','land','other')");
                t.HasCheckConstraint("ck_property_purchase_price", "purchase_price > 0");
                t.HasCheckConstraint("ck_property_fees", "notary_fees >= 0 and agency_fees >= 0 and other_acquisition_costs >= 0");
                t.HasCheckConstraint("ck_property_surface", "surface_m2 is null or surface_m2 > 0");
                t.HasCheckConstraint("ck_property_cadence", "valuation_cadence in ('yearly','manual')");
                t.HasCheckConstraint("ck_property_stale_after_days", "stale_after_days is null or stale_after_days between 1 and 3650");
                t.HasCheckConstraint("ck_property_rental_starts_on", "rental_starts_on is null or rental_starts_on >= purchase_date");
            });
            e.HasKey(x => x.ItemId);
            e.Property(x => x.ItemId).ValueGeneratedNever();
            e.Property(x => x.PurchasePrice).HasColumnType("numeric(19,4)");
            e.Property(x => x.NotaryFees).HasColumnType("numeric(19,4)");
            e.Property(x => x.AgencyFees).HasColumnType("numeric(19,4)");
            e.Property(x => x.OtherAcquisitionCosts).HasColumnType("numeric(19,4)");
            e.Property(x => x.SurfaceM2).HasColumnType("numeric(19,4)");
            MapXmin(e.Property(x => x.Version));
        });

        modelBuilder.MapAuditLog(ownedHere: false);
        ApplySnakeCase(modelBuilder);
    }

    private static void MapXmin(PropertyBuilder<uint> property)
        => property.HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

    private static void ApplySnakeCase(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        foreach (var property in entity.GetProperties())
        {
            if (property.Name == nameof(LoanRow.Version))
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
