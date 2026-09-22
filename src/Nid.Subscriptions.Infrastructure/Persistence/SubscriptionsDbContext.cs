using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Nid.Subscriptions.Infrastructure.Persistence;

public sealed class SubscriptionsDbContext(DbContextOptions<SubscriptionsDbContext> options) : DbContext(options)
{
    public const string Schema = "subscriptions";

    public DbSet<SubscriptionRow> Subscriptions => Set<SubscriptionRow>();
    public DbSet<BillingPhaseRow> Phases => Set<BillingPhaseRow>();
    public DbSet<PurchaseRow> Purchases => Set<PurchaseRow>();
    public DbSet<GrantRow> Grants => Set<GrantRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<SubscriptionRow>(e =>
        {
            e.ToTable("subscription");
            e.HasKey(x => x.ItemId);
            e.Property(x => x.ItemId).ValueGeneratedNever();
            e.HasMany(x => x.Users).WithOne().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Phases).WithOne().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
            MapXmin(e.Property(x => x.Version));
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
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.PriceCurrency).HasColumnType("char(3)");
            e.HasIndex(x => x.ItemId);
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
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.PriceCurrency).HasColumnType("char(3)");
            e.HasMany(x => x.Grants).WithOne().HasForeignKey(x => x.PurchaseId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.Date);
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

        ApplySnakeCase(modelBuilder);
    }

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
