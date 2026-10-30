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

        modelBuilder.MapAuditLog(ownedHere: false);
        ApplySnakeCase(modelBuilder);
    }

    private static void ApplySnakeCase(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        foreach (var property in entity.GetProperties())
            property.SetColumnName(ToSnakeCase(property.Name));

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
