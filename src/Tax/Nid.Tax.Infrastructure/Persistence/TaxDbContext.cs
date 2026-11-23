using Microsoft.EntityFrameworkCore;
using Nid.Platform.Infrastructure.Audit;
using Nid.Platform.Infrastructure.Persistence;

namespace Nid.Tax.Infrastructure.Persistence;

public sealed class TaxDbContext(DbContextOptions<TaxDbContext> options) : DbContext(options)
{
    public const string Schema = "tax";

    public DbSet<TaxProfileRow> TaxProfiles => Set<TaxProfileRow>();
    public DbSet<TaxAssessmentRow> TaxAssessments => Set<TaxAssessmentRow>();
    public DbSet<TaxAssessmentDeclarantRow> TaxAssessmentDeclarants => Set<TaxAssessmentDeclarantRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

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
            // The person lives in Core's schema: no foreign key across contexts (§5.3). The reverse lookup is by person.
            e.HasIndex(x => x.PersonId);
            e.HasOne<TaxAssessmentRow>().WithMany().HasForeignKey(x => x.AssessmentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.MapAuditLog(ownedHere: false);
        ModelNaming.Apply(modelBuilder);
    }

    private static void MapXmin(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<uint> property)
        => property.HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
}
