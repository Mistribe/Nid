using System.Text;
using Microsoft.EntityFrameworkCore;
using Nid.Platform.Infrastructure.Audit;

namespace Nid.Subscriptions.Infrastructure.Persistence;

public sealed class SubscriptionsDbContext(DbContextOptions<SubscriptionsDbContext> options) : DbContext(options)
{
    public const string Schema = "subscriptions";

    public DbSet<SubscriptionRow> Subscriptions => Set<SubscriptionRow>();
    public DbSet<BillingPhaseRow> Phases => Set<BillingPhaseRow>();
    public DbSet<PurchaseRow> Purchases => Set<PurchaseRow>();
    public DbSet<GrantRow> Grants => Set<GrantRow>();
    public DbSet<LabelRow> Labels => Set<LabelRow>();
    public DbSet<ProviderRow> Providers => Set<ProviderRow>();
    public DbSet<CatalogSyncRunRow> CatalogSyncRuns => Set<CatalogSyncRunRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<SubscriptionRow>(e =>
        {
            e.ToTable("subscription", t =>
            {
                ImportConstraint(t, "subscription");
                ImportedByConstraint(t, "subscription");
            });
            e.HasKey(x => x.ItemId);
            e.Property(x => x.ItemId).ValueGeneratedNever();
            e.HasMany(x => x.Users).WithOne().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Phases).WithOne().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
            MapXmin(e.Property(x => x.Version));
            MapImport(e);
            // Design §3.2: a subscription names an existing provider. Restrict: a provider is archived, never deleted.
            e.HasOne<ProviderRow>().WithMany().HasForeignKey(x => x.ProviderKey).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SubscriptionUserRow>(e =>
        {
            e.ToTable("subscription_user");
            e.HasKey(x => new { x.ItemId, x.PersonId });
        });

        modelBuilder.Entity<BillingPhaseRow>(e =>
        {
            e.ToTable("billing_phase", t =>
            {
                t.HasCheckConstraint("ck_billing_phase_range", "ends_on is null or ends_on > starts_on");
                t.HasCheckConstraint("ck_billing_phase_every", "every_count > 0");
                t.HasCheckConstraint("ck_billing_phase_payer",
                    "(payer_type = 'person' and payer_person_id is not null) or (payer_type <> 'person' and payer_person_id is null)");
                t.HasCheckConstraint("ck_billing_phase_price_sign", "price_amount >= 0");
                t.HasCheckConstraint("ck_billing_phase_payer_type", "payer_type in ('person','household')");
                ImportConstraint(t, "billing_phase");
                ImportedByConstraint(t, "billing_phase");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.PriceCurrency).HasColumnType("char(3)");
            e.HasIndex(x => x.ItemId);
            MapImport(e);
        });

        modelBuilder.Entity<PurchaseRow>(e =>
        {
            e.ToTable("purchase", t =>
            {
                t.HasCheckConstraint("ck_purchase_kind",
                    "kind in ('prepaid_term','bundle','gift','adjustment','refund')");
                t.HasCheckConstraint("ck_purchase_allocated",
                    "allocated_amount is null or (kind = 'bundle' and allocated_amount >= 0 and allocated_amount <= price_amount)");
                t.HasCheckConstraint("ck_purchase_price_sign",
                    "price_amount >= 0 or kind in ('adjustment','refund')");
                t.HasCheckConstraint("ck_purchase_payer_type", "payer_type in ('person','household','external')");
                ImportConstraint(t, "purchase");
                ImportedByConstraint(t, "purchase");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.PriceCurrency).HasColumnType("char(3)");
            e.HasMany(x => x.Grants).WithOne().HasForeignKey(x => x.PurchaseId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.Date);
            MapImport(e);
        });

        modelBuilder.Entity<GrantRow>(e =>
        {
            e.ToTable("coverage_grant", t =>
            {
                t.HasCheckConstraint("ck_coverage_grant_duration", "duration_count > 0");
                t.HasCheckConstraint("ck_coverage_grant_source", "source in ('paid','bonus','included','trial','gift')");
            });
            e.HasKey(x => new { x.PurchaseId, x.Index });
            e.HasIndex(x => x.SubscriptionItemId);
        });

        modelBuilder.Entity<LabelRow>(e =>
        {
            e.ToTable("label", t =>
            {
                t.HasCheckConstraint("ck_label_key", "key ~ '^[a-z0-9][a-z0-9-]{0,63}$'");
                t.HasCheckConstraint("ck_label_name", "char_length(name) between 1 and 50");
                t.HasCheckConstraint("ck_label_color", "color ~ '^#[0-9A-Fa-f]{6}$'");
                t.HasCheckConstraint("ck_label_origin", "origin in ('catalog','local')");
            });
            e.HasKey(x => x.Key);
        });

        modelBuilder.Entity<ProviderRow>(e =>
        {
            e.ToTable("provider", t =>
            {
                // An adopted key is whatever 1C1 and 1D3 stored: free text (design §3.4). Every other key has the pattern.
                t.HasCheckConstraint("ck_provider_key", "origin = 'adopted' or key ~ '^[a-z0-9][a-z0-9-]{0,63}$'");
                t.HasCheckConstraint("ck_provider_origin", "origin in ('catalog','custom','adopted')");
                t.HasCheckConstraint("ck_provider_name", "char_length(name) between 1 and 100");
                t.HasCheckConstraint("ck_provider_labels", "cardinality(label_keys) >= 1");
                t.HasCheckConstraint("ck_provider_urls", "char_length(website) <= 2048 and char_length(pricing_page) <= 2048");
                t.HasCheckConstraint("ck_provider_created_by", "(origin = 'custom') = (created_by is not null)");
                t.HasCheckConstraint("ck_provider_commit", "(origin = 'catalog') = (catalog_commit is not null)");
                ImportConstraint(t, "provider");
                // 1C2c design §5: the declared URL is bounded like every URL; the asset is a hash; both icon columns go together.
                t.HasCheckConstraint("ck_provider_icon_url", "char_length(icon_url) <= 2048");
                t.HasCheckConstraint("ck_provider_icon_asset", "icon_asset ~ '^[0-9a-f]{64}$'");
                t.HasCheckConstraint("ck_provider_icon", "(icon_asset is null) = (icon_source is null)");
            });
            e.HasKey(x => x.Key);
            e.Property(x => x.ImportHash).HasColumnType("char(64)");
            e.Property(x => x.IconAsset).HasColumnType("char(64)");
            e.HasIndex(x => x.IconAsset); // the icon route asks whether a hash is a catalog provider's (Task 5)
            // A file-local key means something in its importer's files only (design §3.3): unique per importer.
            e.HasIndex(x => new { x.CreatedBy, x.ImportRef }).IsUnique().HasFilter("import_ref is not null");
            MapXmin(e.Property(x => x.Version));
        });

        modelBuilder.Entity<CatalogSyncRunRow>(e =>
        {
            e.ToTable("catalog_sync_run", t => t.HasCheckConstraint("ck_catalog_sync_run_status", "status in ('running','succeeded','failed')"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Report).HasColumnType("jsonb");
            // At most one running row, as core.fx_sync_run: this is what makes a second sync a 409 (design §4).
            e.HasIndex(x => x.Status).IsUnique().HasFilter("status = 'running'");
            e.HasIndex(x => x.StartedAt);
        });

        modelBuilder.MapAuditLog(ownedHere: false);
        ApplySnakeCase(modelBuilder);
    }

    // A row is imported or it is not: ref and hash come together. Where the ref is unique: see MapImport
    // (subscriptions, phases, purchases) and the provider index (per creator).
    private static void ImportConstraint<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.TableBuilder<T> table, string name)
        where T : class
        => table.HasCheckConstraint($"ck_{name}_import", "(import_ref is null) = (import_hash is null)");

    // Import provenance privacy (spec 2026-09-25 §3): a ref is unique per importer, never across the instance,
    // so an id another adult used privately can still be imported. Rows from before carry no importer.
    private static void MapImport<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> entity)
        where T : class
    {
        entity.Property<string?>("ImportHash").HasColumnType("char(64)");
        entity.HasIndex("ImportedBy", "ImportRef").IsUnique().HasFilter("import_ref is not null");
    }

    private static void ImportedByConstraint<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.TableBuilder<T> table, string name)
        where T : class
        => table.HasCheckConstraint($"ck_{name}_imported_by", "imported_by is null or import_ref is not null");

    private static void MapXmin(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<uint> property)
        => property.HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

    private static void ApplySnakeCase(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        foreach (var property in entity.GetProperties())
        {
            if (property.Name == nameof(SubscriptionRow.Version))
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
