using Microsoft.EntityFrameworkCore;
using Nid.Platform.Infrastructure.Audit;
using Nid.Platform.Infrastructure.Persistence;

namespace Nid.Assets.Infrastructure.Persistence;

public sealed class AssetsDbContext(DbContextOptions<AssetsDbContext> options) : DbContext(options)
{
    public const string Schema = "assets";

    public DbSet<AssetRow> Assets => Set<AssetRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<AssetRow>(e =>
        {
            e.ToTable("asset", t =>
            {
                t.HasCheckConstraint("ck_asset_purchase", "(purchase_price is null) = (purchased_on is null)");
                t.HasCheckConstraint("ck_asset_metal", "kind = 'precious_metal' or (weight_grams is null and fineness is null)");
                t.HasCheckConstraint("ck_asset_vehicle",
                    "kind = 'vehicle' or (registered_on is null and mileage_km is null and mileage_on is null and fiscal_hp is null and energy is null)");
                t.HasCheckConstraint("ck_asset_mileage", "(mileage_km is null) = (mileage_on is null)");
                t.HasCheckConstraint("ck_asset_import",
                    "(import_ref is null) = (import_hash is null) and (import_ref is null) = (imported_by is null)");
            });
            e.HasKey(x => x.ItemId);
            e.Property(x => x.ItemId).ValueGeneratedNever();
            e.Property(x => x.PurchasePrice).HasColumnType("numeric(18,2)");
            e.Property(x => x.WeightGrams).HasColumnType("numeric(12,3)");
            e.Property(x => x.Fineness).HasColumnType("numeric(5,1)");
            e.HasIndex(x => x.Kind);
            e.Property(x => x.ImportHash).HasColumnType("char(64)");
            // Per importer, never instance-wide (import-format.md section 8).
            e.HasIndex(x => new { x.ImportedBy, x.ImportRef }).IsUnique().HasFilter("import_ref is not null");
            e.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        });

        modelBuilder.MapAuditLog(ownedHere: false);
        ModelNaming.Apply(modelBuilder);
    }
}
