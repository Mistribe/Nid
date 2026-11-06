using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Nid.Platform.Infrastructure.Audit;

namespace Nid.Core.Infrastructure.Persistence;

public sealed class CoreDbContext(DbContextOptions<CoreDbContext> options) : DbContext(options)
{
    public const string Schema = "core";

    public DbSet<HouseholdSettingsRow> HouseholdSettings => Set<HouseholdSettingsRow>();
    public DbSet<PersonRow> Persons => Set<PersonRow>();
    public DbSet<ItemRow> Items => Set<ItemRow>();
    public DbSet<OwnershipRow> Ownerships => Set<OwnershipRow>();
    public DbSet<FxRateRow> FxRates => Set<FxRateRow>();
    public DbSet<FxSyncRunRow> FxSyncRuns => Set<FxSyncRunRow>();
    public DbSet<CpiRow> Cpi => Set<CpiRow>();
    public DbSet<CpiRunRow> CpiRuns => Set<CpiRunRow>();
    public DbSet<ValuationRow> Valuations => Set<ValuationRow>();
    public DbSet<FlowRow> Flows => Set<FlowRow>();
    public DbSet<PersonalAccessTokenRow> PersonalAccessTokens => Set<PersonalAccessTokenRow>();
    public DbSet<ProposalRow> Proposals => Set<ProposalRow>();
    public DbSet<TaxProfileRow> TaxProfiles => Set<TaxProfileRow>();
    public DbSet<TaxAssessmentRow> TaxAssessments => Set<TaxAssessmentRow>();
    public DbSet<TaxAssessmentDeclarantRow> TaxAssessmentDeclarants => Set<TaxAssessmentDeclarantRow>();
    public DbSet<ScenarioRow> Scenarios => Set<ScenarioRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<HouseholdSettingsRow>(e =>
        {
            e.ToTable("household_settings", t =>
            {
                t.HasCheckConstraint("ck_household_settings_single_row", "id = 1");
                t.HasCheckConstraint("ck_household_settings_max_adults", "max_adults >= 1");
                t.HasCheckConstraint("ck_household_settings_bank_retention", "bank_retention_months between 1 and 600");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.ReferenceCurrency).HasColumnType("char(3)");
            e.Property(x => x.AllocationTarget).HasColumnType("jsonb");
            e.Property(x => x.ProjectionAssumptions).HasColumnType("jsonb");
            e.Property(x => x.FeeReferences).HasColumnType("jsonb");
            e.Property(x => x.MaxAdults).HasDefaultValue(2);
            e.Property(x => x.PricesEnabled).HasDefaultValue(false);
            e.Property(x => x.IrlEnabled).HasDefaultValue(false);
            e.Property(x => x.InflationEnabled).HasDefaultValue(true);
            e.Property(x => x.BankingEnabled).HasDefaultValue(false);
        });

        // Tax estimate spec §7: one household document per revenue year.
        modelBuilder.Entity<TaxProfileRow>(e =>
        {
            e.ToTable("tax_profile", t => t.HasCheckConstraint("ck_tax_profile_year", "year between 2025 and 2100"));
            e.HasKey(x => x.Year);
            e.Property(x => x.Year).ValueGeneratedNever();
            e.Property(x => x.Data).HasColumnType("jsonb");
        });

        // Tax assessments spec §3: a notice and its declarants; a person is in one notice per year.
        modelBuilder.Entity<TaxAssessmentRow>(e =>
        {
            e.ToTable("tax_assessment", t => t.HasCheckConstraint("ck_tax_assessment_year", "year between 2000 and 2100"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Data).HasColumnType("jsonb");
            MapXmin(e.Property(x => x.Version));
        });

        modelBuilder.Entity<TaxAssessmentDeclarantRow>(e =>
        {
            e.ToTable("tax_assessment_declarant", t => t.HasCheckConstraint("ck_tax_assessment_declarant_rank", "rank in (1,2)"));
            e.HasKey(x => new { x.AssessmentId, x.PersonId });
            e.HasIndex(x => new { x.Year, x.PersonId }).IsUnique();
            e.HasOne<TaxAssessmentRow>().WithMany().HasForeignKey(x => x.AssessmentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<PersonRow>().WithMany().HasForeignKey(x => x.PersonId).OnDelete(DeleteBehavior.Restrict);
        });

        // Property purchase scenario spec §5: the document of a registry item.
        modelBuilder.Entity<ScenarioRow>(e =>
        {
            e.ToTable("scenario", t =>
            {
                t.HasCheckConstraint("ck_scenario_kind", "kind in ('property_purchase','projection','property_sale')");
                t.HasCheckConstraint("ck_scenario_scope", "scope in ('me','household')");
            });
            e.HasKey(x => x.ItemId);
            e.Property(x => x.ItemId).ValueGeneratedNever();
            e.Property(x => x.Data).HasColumnType("jsonb");
            e.HasOne<ItemRow>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PersonRow>(e =>
        {
            e.ToTable("person", t =>
            {
                t.HasCheckConstraint("ck_person_kind", "kind in ('adult','dependent')");
                // An adult is active (a subject) or pending (an email to be recognised by); a dependent never signs in.
                t.HasCheckConstraint("ck_person_subject",
                    "(kind = 'adult' and (oidc_subject is not null or email is not null)) or (kind = 'dependent' and oidc_subject is null)");
                t.HasCheckConstraint("ck_person_admin", "kind = 'adult' or not is_admin");
                t.HasCheckConstraint("ck_person_retirement_age", "retirement_age between 50 and 75");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Preferences).HasColumnType("jsonb");
            e.Property(x => x.IsAdmin).HasDefaultValue(false);
            e.Property(x => x.RetirementAge).HasDefaultValue(64);
            e.HasIndex(x => x.OidcSubject).IsUnique().HasFilter("oidc_subject is not null");
            // ix_person_adult_email (unique on lower(email) among adults) is an expression index: created by the
            // AdminArea migration in SQL, EF Core has no model for it.
            MapXmin(e.Property(x => x.Version));
        });

        modelBuilder.Entity<PersonalAccessTokenRow>(e =>
        {
            e.ToTable("personal_access_token", t =>
            {
                t.HasCheckConstraint("ck_personal_access_token_expiry", "expires_at > created_at");
                t.HasCheckConstraint("ck_personal_access_token_scopes",
                    "scopes = '{read}'::text[] or scopes = '{read,write}'::text[] or scopes = '{read,write,write:direct}'::text[]");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.TokenHash).HasColumnType("char(64)");
            e.HasIndex(x => x.Prefix).IsUnique();
            e.HasIndex(x => x.PersonId);
            e.HasOne<PersonRow>().WithMany().HasForeignKey(x => x.PersonId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ProposalRow>(e =>
        {
            e.ToTable("proposal", t =>
            {
                t.HasCheckConstraint("ck_proposal_status", "status in ('pending','approved','rejected','superseded')");
                t.HasCheckConstraint("ck_proposal_created_via", "created_via in ('ui','api','mcp','job')");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.Property(x => x.EditedPayload).HasColumnType("jsonb");
            e.Property(x => x.Warnings).HasColumnType("jsonb");
            e.Property(x => x.Fingerprint).HasColumnType("char(64)");
            e.Property(x => x.AppliedRefs).HasDefaultValueSql("'{}'");
            e.HasIndex(x => x.Fingerprint).IsUnique().HasFilter("status = 'pending'");
            e.HasIndex(x => new { x.Kind, x.TargetItemId, x.SubjectKey }).HasFilter("status = 'pending'");
            e.HasIndex(x => new { x.TargetItemId, x.Status });
            e.HasOne<ItemRow>().WithMany().HasForeignKey(x => x.TargetItemId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<PersonRow>().WithMany().HasForeignKey(x => x.ForPersonId).OnDelete(DeleteBehavior.Restrict);
            MapXmin(e.Property(x => x.Version));
        });

        modelBuilder.Entity<ItemRow>(e =>
        {
            e.ToTable("item", t =>
            {
                t.HasCheckConstraint("ck_item_visibility", "visibility in ('shared','private')");
                t.HasCheckConstraint("ck_item_share_level", "share_level = 'view' or (share_level = 'edit' and visibility = 'shared')");
            });
            e.Property(x => x.ShareLevel).HasDefaultValue("view");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Currency).HasColumnType("char(3)");
            e.HasMany(x => x.Owners).WithOne().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.Module, x.Kind });
            MapXmin(e.Property(x => x.Version));
        });

        modelBuilder.Entity<OwnershipRow>(e =>
        {
            e.ToTable("ownership", t => t.HasCheckConstraint("ck_ownership_share", "share_bp between 1 and 10000"));
            e.HasKey(x => new { x.ItemId, x.PersonId });
            e.HasOne<PersonRow>().WithMany().HasForeignKey(x => x.PersonId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FxRateRow>(e =>
        {
            e.ToTable("fx_rate", t =>
            {
                t.HasCheckConstraint("ck_fx_rate_positive", "rate > 0");
                t.HasCheckConstraint("ck_fx_rate_not_usd", "quote <> 'USD'");
                t.HasCheckConstraint("ck_fx_rate_source", "source in ('ecb','subtracker_data','manual')");
            });
            e.HasKey(x => new { x.OnDate, x.Quote });
            // The reads go by quote first (FxRateStore.ReadAsync, PreviousAsync's distinct on): the key leads with the date.
            e.HasIndex(x => new { x.Quote, x.OnDate });
            e.Property(x => x.Quote).HasColumnType("char(3)");
            e.Property(x => x.Rate).HasColumnType("numeric(19,10)");
            e.Property(x => x.FetchedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<CpiRow>(e =>
        {
            e.ToTable("cpi", t =>
            {
                t.HasCheckConstraint("ck_cpi_month", "month ~ '^[0-9]{4}-(0[1-9]|1[0-2])$'");
                t.HasCheckConstraint("ck_cpi_value", "value > 0");
                t.HasCheckConstraint("ck_cpi_source", "source in ('insee','manual')");
            });
            e.HasKey(x => x.Month);
            e.Property(x => x.Month).ValueGeneratedNever();
            e.Property(x => x.Value).HasColumnType("numeric(10,4)");
        });

        modelBuilder.Entity<CpiRunRow>(e =>
        {
            e.ToTable("cpi_run", t => t.HasCheckConstraint("ck_cpi_run_status", "status in ('running','succeeded','failed')"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            // At most one running row: a second start fails on this index.
            e.HasIndex(x => x.Status).IsUnique().HasFilter("status = 'running'");
            e.HasIndex(x => x.StartedAt);
        });

        modelBuilder.Entity<FxSyncRunRow>(e =>
        {
            e.ToTable("fx_sync_run", t =>
            {
                t.HasCheckConstraint("ck_fx_sync_run_kind", "kind in ('daily','backfill')");
                t.HasCheckConstraint("ck_fx_sync_run_status", "status in ('running','succeeded','failed')");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Rejected).HasColumnType("jsonb");
            // At most one running row: every running row has the same status, so a unique partial index on it
            // admits only one. This is what turns a second POST /sync into a 409 (design §6.3).
            e.HasIndex(x => x.Status).IsUnique().HasFilter("status = 'running'");
            e.HasIndex(x => x.StartedAt);
        });

        const string createdVia = "created_via in ('ui','api','mcp','job','import','admin-cli')";

        modelBuilder.Entity<ValuationRow>(e =>
        {
            e.ToTable("valuation", t =>
            {
                t.HasCheckConstraint("ck_valuation_source", "source in ('manual','import','bank_api')");
                t.HasCheckConstraint("ck_valuation_created_via", createdVia);
                // 2B1b: an imported fact keeps its file id per importer (import-format.md §8); a correction retires the old row and
                // the new one takes the reference over, hence the filter on current rows.
                t.HasCheckConstraint("ck_valuation_import", "(import_ref is null) = (import_hash is null) and (import_ref is null) = (imported_by is null)");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Amount).HasColumnType("numeric(19,4)");
            e.Property(x => x.Currency).HasColumnType("char(3)");
            e.HasOne<ItemRow>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<PersonRow>().WithMany().HasForeignKey(x => x.CreatedByPersonId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ValuationRow>().WithMany().HasForeignKey(x => x.SupersedesId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.ImportHash).HasColumnType("char(64)");
            e.HasOne<PersonRow>().WithMany().HasForeignKey(x => x.ImportedBy).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.ImportedBy, x.ImportRef }).IsUnique()
                .HasFilter("import_ref is not null and retired_at is null").HasDatabaseName("ux_valuation_import_ref");
            // At most one current value per item and date; it also serves "the last value on or before d".
            e.HasIndex(x => new { x.ItemId, x.AsOf }).IsUnique().HasFilter("retired_at is null").HasDatabaseName("ux_valuation_current");
            MapXmin(e.Property(x => x.Version));
        });

        modelBuilder.Entity<FlowRow>(e =>
        {
            e.ToTable("flow", t =>
            {
                t.HasCheckConstraint("ck_flow_amount_positive", "amount > 0");
                t.HasCheckConstraint("ck_flow_kind", "kind in ('contribution','withdrawal','opening','income','expense')");
                t.HasCheckConstraint("ck_flow_source", "source in ('manual','import')");
                t.HasCheckConstraint("ck_flow_created_via", createdVia);
                // 2B1b: an imported fact keeps its file id per importer (import-format.md §8); a correction retires the old row and
                // the new one takes the reference over, hence the filter on current rows.
                t.HasCheckConstraint("ck_flow_import", "(import_ref is null) = (import_hash is null) and (import_ref is null) = (imported_by is null)");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Amount).HasColumnType("numeric(19,4)");
            e.Property(x => x.Currency).HasColumnType("char(3)");
            e.HasOne<ItemRow>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<PersonRow>().WithMany().HasForeignKey(x => x.CreatedByPersonId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<FlowRow>().WithMany().HasForeignKey(x => x.SupersedesId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.ImportHash).HasColumnType("char(64)");
            e.HasOne<PersonRow>().WithMany().HasForeignKey(x => x.ImportedBy).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.ImportedBy, x.ImportRef }).IsUnique()
                .HasFilter("import_ref is not null and retired_at is null").HasDatabaseName("ux_flow_import_ref");
            // At most one current opening per item (core-data-model.md §5.3).
            e.HasIndex(x => x.ItemId).IsUnique().HasFilter("kind = 'opening' and retired_at is null").HasDatabaseName("ux_flow_opening_current");
            // An external reference makes a re-import without duplicates (import-format.md §5.6).
            e.HasIndex(x => new { x.ItemId, x.ExternalRef }).IsUnique().HasFilter("external_ref is not null and retired_at is null")
                .HasDatabaseName("ux_flow_external_ref_current");
            e.HasIndex(x => new { x.ItemId, x.OccurredOn }).HasFilter("retired_at is null");
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
            if (property.Name == nameof(PersonRow.Version))
                continue; // mapped to the system column xmin

            property.SetColumnName(ToSnakeCase(property.Name));
        }

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            var table = entity.GetTableName()!;
            entity.FindPrimaryKey()?.SetName($"pk_{table}");

            // An index named in the model (HasDatabaseName) keeps its name: the partial unique ones are named for what they guard.
            foreach (var index in entity.GetIndexes().Where(i => i.FindAnnotation(RelationalAnnotationNames.Name) is null))
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
