using Nid.Platform.Infrastructure.Persistence;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Nid.Documents.Contracts;
using Nid.Platform.Infrastructure.Audit;

namespace Nid.Documents.Infrastructure.Persistence;

public sealed class DocumentsDbContext(DbContextOptions<DocumentsDbContext> options) : DbContext(options)
{
    public const string Schema = "documents";

    public const string SourceRefIndex = "ix_document_source_ref";

    public DbSet<DocumentRow> Documents => Set<DocumentRow>();

    public DbSet<DocumentLinkRow> Links => Set<DocumentLinkRow>();

    public DbSet<UploadTicketRow> UploadTickets => Set<UploadTicketRow>();

    public DbSet<ExpectedDismissalRow> ExpectedDismissals => Set<ExpectedDismissalRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<DocumentRow>(e =>
        {
            e.ToTable("document");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Sha256).HasColumnType("char(64)");
            e.Property(x => x.SourceRef).HasMaxLength(200);
            e.HasIndex(x => x.SourceRef).IsUnique().HasFilter("source_ref is not null").HasDatabaseName(SourceRefIndex);
            e.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        });

        modelBuilder.Entity<DocumentLinkRow>(e =>
        {
            var roles = string.Join(", ", DocumentRoles.All.Order().Select(r => $"'{r}'"));
            e.ToTable("document_link", t => t.HasCheckConstraint("ck_document_link_role", $"role in ({roles})"));
            e.HasKey(x => new { x.DocumentId, x.TargetKind, x.TargetId });
            e.HasIndex(x => new { x.TargetKind, x.TargetId });
            e.HasIndex(x => x.AnchorItemId);
            e.HasOne<DocumentRow>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UploadTicketRow>(e =>
        {
            e.ToTable("upload_ticket");
            e.HasKey(x => x.TokenHash);
            e.Property(x => x.TokenHash).HasColumnType("char(64)");
            e.Property(x => x.Title).HasMaxLength(255);
            e.Property(x => x.Note).HasMaxLength(2000);
            e.Property(x => x.SourceRef).HasMaxLength(200);
        });

        modelBuilder.Entity<ExpectedDismissalRow>(e =>
        {
            e.ToTable("expected_dismissal");
            e.HasKey(x => new { x.TargetKind, x.TargetId, x.Role, x.Period });
        });

        modelBuilder.MapAuditLog(ownedHere: false);
        ModelNaming.Apply(modelBuilder);
    }
}
