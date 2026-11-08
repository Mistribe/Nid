using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Nid.Platform.Infrastructure.Audit;

/// <summary>
/// One line of <c>platform.audit_log</c> (1F3a spec §3). Written by every module's context, so an entry lands in
/// the same transaction as the write it records; read by Platform only.
/// </summary>
public sealed class AuditEntryRow
{
    public long Seq { get; set; }
    public Guid Id { get; set; }
    public Instant At { get; set; }
    public string Category { get; set; } = "";
    public string Action { get; set; } = "";
    public string ActorKind { get; set; } = "";
    public Guid? PersonId { get; set; }
    public string Via { get; set; } = "";
    public string[] Scopes { get; set; } = [];

    /// <summary>4A: the personal access token the actor used; null for a session, the system and the CLI.</summary>
    public Guid? TokenId { get; set; }
    public string? RequestId { get; set; }
    public string? Request { get; set; }
    public string? Entity { get; set; }
    public string? EntityKey { get; set; }
    public Guid[] ItemIds { get; set; } = [];
    public Guid? SubjectPersonId { get; set; }
    public string Data { get; set; } = "{}";
}

public static class AuditLogMapping
{
    public const string Schema = "platform";
    public const string Table = "audit_log";

    /// <summary>
    /// Maps the log onto <paramref name="modelBuilder"/>. Only Platform owns it (<paramref name="ownedHere"/>):
    /// the module contexts exclude it from their migrations. Call before the context's snake-case pass.
    /// </summary>
    public static ModelBuilder MapAuditLog(this ModelBuilder modelBuilder, bool ownedHere)
    {
        modelBuilder.Entity<AuditEntryRow>(e =>
        {
            e.ToTable(Table, Schema, t =>
            {
                if (!ownedHere)
                    t.ExcludeFromMigrations();
                t.HasCheckConstraint("ck_audit_log_category", "category in ('write','security','import')");
            });
            e.HasKey(x => x.Seq);
            e.Property(x => x.Seq).UseIdentityAlwaysColumn();
            e.Property(x => x.Data).HasColumnType("jsonb");
            e.Property(x => x.ItemIds).HasDefaultValueSql("'{}'");
            e.HasIndex(x => x.ItemIds).HasMethod("gin");
            e.HasIndex(x => new { x.SubjectPersonId, x.Seq }).HasFilter("category = 'security'");
            e.HasIndex(x => new { x.Category, x.Action, x.Seq });
        });
        return modelBuilder;
    }
}
