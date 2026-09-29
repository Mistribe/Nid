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
    public DbSet<ValuationRow> Valuations => Set<ValuationRow>();
    public DbSet<FlowRow> Flows => Set<FlowRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<HouseholdSettingsRow>(e =>
        {
            e.ToTable("household_settings", t =>
            {
                t.HasCheckConstraint("ck_household_settings_single_row", "id = 1");
                t.HasCheckConstraint("ck_household_settings_max_adults", "max_adults >= 1");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.ReferenceCurrency).HasColumnType("char(3)");
            e.Property(x => x.MaxAdults).HasDefaultValue(2);
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
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Preferences).HasColumnType("jsonb");
            e.Property(x => x.IsAdmin).HasDefaultValue(false);
            e.HasIndex(x => x.OidcSubject).IsUnique().HasFilter("oidc_subject is not null");
            // ix_person_adult_email (unique on lower(email) among adults) is an expression index: created by the
            // AdminArea migration in SQL, EF Core has no model for it.
            MapXmin(e.Property(x => x.Version));
        });

        modelBuilder.Entity<ItemRow>(e =>
        {
            e.ToTable("item", t => t.HasCheckConstraint("ck_item_visibility", "visibility in ('shared','private')"));
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
                t.HasCheckConstraint("ck_valuation_source", "source in ('manual','import')");
                t.HasCheckConstraint("ck_valuation_created_via", createdVia);
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Amount).HasColumnType("numeric(19,4)");
            e.Property(x => x.Currency).HasColumnType("char(3)");
            e.HasOne<ItemRow>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<PersonRow>().WithMany().HasForeignKey(x => x.CreatedByPersonId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ValuationRow>().WithMany().HasForeignKey(x => x.SupersedesId).OnDelete(DeleteBehavior.Restrict);
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
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Amount).HasColumnType("numeric(19,4)");
            e.Property(x => x.Currency).HasColumnType("char(3)");
            e.HasOne<ItemRow>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<PersonRow>().WithMany().HasForeignKey(x => x.CreatedByPersonId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<FlowRow>().WithMany().HasForeignKey(x => x.SupersedesId).OnDelete(DeleteBehavior.Restrict);
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
