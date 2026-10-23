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

    public DbSet<LoanBankScheduleRow> BankSchedule => Set<LoanBankScheduleRow>();

    public DbSet<PropertyRow> Properties => Set<PropertyRow>();

    public DbSet<PropertyRecurringCostRow> RecurringCosts => Set<PropertyRecurringCostRow>();

    public DbSet<PropertyTenancyRow> Tenancies => Set<PropertyTenancyRow>();

    public DbSet<IrlRow> Irl => Set<IrlRow>();

    public DbSet<IrlRunRow> IrlRuns => Set<IrlRunRow>();

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
                t.HasCheckConstraint("ck_loan_import", ImportCheck);
                t.HasCheckConstraint("ck_loan_schedule_source", "schedule_source in ('computed','bank')");
            });
            e.HasKey(x => x.ItemId);
            e.Property(x => x.ItemId).ValueGeneratedNever();
            e.Property(x => x.Principal).HasColumnType("numeric(19,4)");
            e.HasIndex(x => x.PropertyItemId);
            e.Property(x => x.ScheduleSource).HasDefaultValue("computed"); // the loans stored before 3F2 read the calculation
            MapXmin(e.Property(x => x.Version));
        });

        modelBuilder.Entity<LoanPhaseRow>(e =>
        {
            e.ToTable("loan_phase", t =>
            {
                t.HasCheckConstraint("ck_loan_phase_mode", "mode in ('annuity','deferred_partial','deferred_total')");
                t.HasCheckConstraint("ck_loan_phase_rate_type", "rate_type in ('fixed','variable')");
                t.HasCheckConstraint("ck_loan_phase_annual_rate", "annual_rate >= 0 and annual_rate < 1");
                t.HasCheckConstraint("ck_loan_phase_import", ImportCheck);
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
            {
                t.HasCheckConstraint("ck_loan_insurance_mode", "mode in ('fixed_monthly','rate_on_initial','rate_on_remaining')");
                t.HasCheckConstraint("ck_loan_insurance_import", ImportCheck);
            });
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
                t.HasCheckConstraint("ck_loan_fee_import", ImportCheck);
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
                t.HasCheckConstraint("ck_loan_event_import", ImportCheck);
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Amount).HasColumnType("numeric(19,4)");
            e.HasIndex(x => x.LoanItemId);
            MapXmin(e.Property(x => x.Version));
        });

        // Spec 3F2 §2: the key is (loan, due date); the row id is the import's (a file id resolves to a row).
        modelBuilder.Entity<LoanBankScheduleRow>(e =>
        {
            e.ToTable("loan_bank_schedule_row", t =>
            {
                t.HasCheckConstraint("ck_loan_bank_schedule_row_amounts",
                    "payment >= 0 and interest >= 0 and principal >= 0 and (insurance is null or insurance >= 0) and crd >= 0");
                t.HasCheckConstraint("ck_loan_bank_schedule_row_import", ImportCheck);
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Payment).HasColumnType("numeric(19,4)");
            e.Property(x => x.Interest).HasColumnType("numeric(19,4)");
            e.Property(x => x.Principal).HasColumnType("numeric(19,4)");
            e.Property(x => x.Insurance).HasColumnType("numeric(19,4)");
            e.Property(x => x.Crd).HasColumnType("numeric(19,4)");
            e.HasIndex(x => new { x.LoanItemId, x.DueDate }).IsUnique();
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
                t.HasCheckConstraint("ck_property_energy_class", "energy_class is null or energy_class in ('A','B','C','D','E','F','G')");
                t.HasCheckConstraint("ck_property_import", ImportCheck);
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

        // Spec 3C2 §2. No foreign key to the property, as the loan's link: a property is never deleted.
        modelBuilder.Entity<PropertyRecurringCostRow>(e =>
        {
            e.ToTable("property_recurring_cost", t =>
            {
                t.HasCheckConstraint("ck_property_recurring_cost_category",
                    "category in ('property_tax','condo_fees','insurance','energy','maintenance','other')");
                t.HasCheckConstraint("ck_property_recurring_cost_amount", "amount > 0");
                t.HasCheckConstraint("ck_property_recurring_cost_every", "every_count between 1 and 120 and every_unit in ('month','year')");
                t.HasCheckConstraint("ck_property_recurring_cost_dates", "ends_on is null or ends_on > starts_on");
                t.HasCheckConstraint("ck_property_recurring_cost_import", ImportCheck);
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Amount).HasColumnType("numeric(19,4)");
            e.HasIndex(x => x.PropertyItemId);
            MapXmin(e.Property(x => x.Version));
        });

        modelBuilder.Entity<PropertyTenancyRow>(e =>
        {
            e.ToTable("property_tenancy", t =>
            {
                t.HasCheckConstraint("ck_property_tenancy_amounts", "rent_monthly > 0 and charges_monthly >= 0 and (deposit is null or deposit > 0)");
                t.HasCheckConstraint("ck_property_tenancy_dates", "ends_on is null or ends_on > starts_on");
                t.HasCheckConstraint("ck_property_tenancy_irl_quarter", "irl_reference_quarter is null or irl_reference_quarter between 1 and 4");
                t.HasCheckConstraint("ck_property_tenancy_indexed", "not indexed or irl_reference_quarter is not null");
                t.HasCheckConstraint("ck_property_tenancy_import", ImportCheck);
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.RentMonthly).HasColumnType("numeric(19,4)");
            e.Property(x => x.ChargesMonthly).HasColumnType("numeric(19,4)");
            e.Property(x => x.Deposit).HasColumnType("numeric(19,4)");
            e.Property(x => x.Indexed).HasDefaultValue(false);
            e.HasIndex(x => x.PropertyItemId);
            // A revision keeps its predecessor: it is deleted first, never the other way (rental spec §3.2).
            e.HasOne<PropertyTenancyRow>().WithMany().HasForeignKey(x => x.RevisedFrom).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.RevisedFrom);
            MapXmin(e.Property(x => x.Version));
        });

        modelBuilder.Entity<IrlRow>(e =>
        {
            e.ToTable("irl", t =>
            {
                t.HasCheckConstraint("ck_irl_quarter", "quarter ~ '^[0-9]{4}-Q[1-4]$'");
                t.HasCheckConstraint("ck_irl_value", "value > 0");
                t.HasCheckConstraint("ck_irl_source", "source in ('insee','manual')");
            });
            e.HasKey(x => x.Quarter);
            e.Property(x => x.Quarter).ValueGeneratedNever();
            e.Property(x => x.Value).HasColumnType("numeric(10,4)");
        });

        modelBuilder.Entity<IrlRunRow>(e =>
        {
            e.ToTable("irl_run", t => t.HasCheckConstraint("ck_irl_run_status", "status in ('running','succeeded','failed')"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            // At most one running row, as investments.price_run: a second start fails on this index.
            e.HasIndex(x => x.Status).IsUnique().HasFilter("status = 'running'");
            e.HasIndex(x => x.StartedAt);
        });

        // Spec 3F1 §2: every table an import writes keeps the file id it came from, unique per importer (import-format.md §8);
        // no foreign key to core.person across contexts (2B1a §3.2).
        foreach (var entity in modelBuilder.Model.GetEntityTypes().Where(t => typeof(IImportMarked).IsAssignableFrom(t.ClrType)).ToList())
        {
            var builder = modelBuilder.Entity(entity.ClrType);
            builder.Property(nameof(IImportMarked.ImportHash)).HasColumnType("char(64)");
            builder.HasIndex(nameof(IImportMarked.ImportedBy), nameof(IImportMarked.ImportRef)).IsUnique().HasFilter("import_ref is not null");
        }

        modelBuilder.MapAuditLog(ownedHere: false);
        ApplySnakeCase(modelBuilder);
    }

    private static void MapXmin(PropertyBuilder<uint> property)
        => property.HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

    /// <summary>A row is imported or it is not: ref, hash and importer come together (as investments.envelope).</summary>
    private const string ImportCheck = "(import_ref is null) = (import_hash is null) and (import_ref is null) = (imported_by is null)";

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
