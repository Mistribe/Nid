using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Nid.Platform.Audit;
using Nid.Platform.Contracts;
using Nid.Platform.Import;
using Nid.Platform.Infrastructure.Persistence;
using NodaTime;

namespace Nid.Platform.Infrastructure.Audit;

/// <summary>
/// 1F3b: the <c>import</c> entry. Written through <see cref="PlatformDbContext"/>, which shares the commit's connection and
/// transaction (UnitOfWork), so a commit that rolls back leaves no entry. <see cref="AuditEntryRow"/> is excluded from the
/// interceptor: this row is not audited again.
/// </summary>
public sealed class ImportAuditLog(PlatformDbContext db, IActorAccessor actors, AuditRequest request, IClock clock) : IImportAudit
{
    public async Task RecordAsync(ImportCommitRecord record, CancellationToken ct)
    {
        var actor = actors.Current ?? throw new InvalidOperationException("An import is committed by a person.");
        var counts = new JsonObject();
        foreach (var (section, c) in record.Counts.OrderBy(p => p.Key, StringComparer.Ordinal))
            counts[section] = new JsonObject { ["created"] = c.Created, ["unchanged"] = c.Unchanged, ["replaced"] = c.Replaced };

        db.AuditLog.Add(new AuditEntryRow
        {
            Id = Guid.CreateVersion7(),
            At = clock.GetCurrentInstant(),
            Category = "import",
            Action = "commit",
            ActorKind = "person",
            PersonId = record.Importer.Value,
            Via = actor.Via.ToWire(),
            Scopes = actor.Scopes.Order(StringComparer.Ordinal).ToArray(),
            RequestId = Activity.Current?.TraceId.ToString(),
            Request = request.Name,
            Entity = "import_session",
            EntityKey = record.Session.ToString(),
            ItemIds = [],
            Data = new JsonObject
            {
                ["session"] = record.Session.ToString(),
                ["content_hash"] = record.ContentHash,
                ["format"] = NidDataFormat.Name,
                ["version"] = NidDataFormat.Version,
                ["replace"] = record.Replace,
                ["counts"] = counts,
            }.ToJsonString(),
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<ImportAuditView?> FindAsync(Guid session, CancellationToken ct)
    {
        var key = session.ToString();
        var row = await db.AuditLog.AsNoTracking()
            .Where(r => r.Category == "import" && r.Entity == "import_session" && r.EntityKey == key)
            .OrderByDescending(r => r.Seq).FirstOrDefaultAsync(ct);
        if (row is null)
            return null;
        using var data = JsonDocument.Parse(row.Data);
        var root = data.RootElement;
        return new ImportAuditView(row.At.ToDateTimeOffset(), root.GetProperty("content_hash").GetString()!,
            root.GetProperty("format").GetString()!, root.GetProperty("version").GetInt32(), root.GetProperty("replace").GetBoolean());
    }
}
