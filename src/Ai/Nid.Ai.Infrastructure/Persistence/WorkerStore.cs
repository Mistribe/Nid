using Microsoft.EntityFrameworkCore;
using Nid.Ai.Application;
using Nid.Ai.Contracts;
using NodaTime;

namespace Nid.Ai.Infrastructure.Persistence;

public sealed class WorkerStore(AiDbContext db) : IWorkerStore
{
    public async Task<IReadOnlyList<WorkerRecord>> ListAsync(CancellationToken ct)
        => (await db.Workers.AsNoTracking().OrderBy(w => w.Name).ToListAsync(ct)).Select(ToRecord).ToList();

    public async Task<WorkerRecord?> FindAsync(Guid id, CancellationToken ct)
        => await db.Workers.AsNoTracking().SingleOrDefaultAsync(w => w.Id == id, ct) is { } row ? ToRecord(row) : null;

    public async Task<WorkerRecord?> FindByEndpointAsync(string host, int port, CancellationToken ct)
        => await db.Workers.AsNoTracking().SingleOrDefaultAsync(w => w.Host == host && w.Port == port && w.Status != "revoked", ct)
            is { } row ? ToRecord(row) : null;

    public async Task AddAsync(WorkerRecord worker, CancellationToken ct)
    {
        db.Workers.Add(Copy(worker, new ModelWorkerRow
        {
            Id = worker.Id, Name = worker.Name, Host = worker.Host, Placement = "", Status = "",
            WorkerPublicKey = worker.WorkerPublicKey, WorkerKeyFingerprint = worker.WorkerKeyFingerprint,
        }));
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> UpdateAsync(WorkerRecord worker, CancellationToken ct)
    {
        var row = await db.Workers.SingleAsync(w => w.Id == worker.Id, ct);
        db.Entry(row).Property(w => w.Version).OriginalValue = worker.Version;
        Copy(worker, row);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    public async Task DropPendingCreatedBeforeAsync(Instant before, CancellationToken ct)
    {
        var stale = await db.Workers.Where(w => w.Status == "pending" && w.CreatedAt < before).ToListAsync(ct);
        db.Workers.RemoveRange(stale);
        await db.SaveChangesAsync(ct);
    }

    private static ModelWorkerRow Copy(WorkerRecord from, ModelWorkerRow to)
    {
        to.Name = from.Name;
        to.Host = from.Host;
        to.Port = from.Port;
        to.ProxyDeclared = from.ProxyDeclared;
        to.Placement = from.Placement == WorkerPlacement.Local ? "local" : "remote";
        to.Safe = from.Safe;
        to.Status = from.Status.ToString().ToLowerInvariant();
        to.WorkerPublicKey = from.WorkerPublicKey;
        to.WorkerKeyFingerprint = from.WorkerKeyFingerprint;
        to.PreviousWorkerPublicKey = from.PreviousWorkerPublicKey;
        to.PreviousWorkerKeyUntil = from.PreviousWorkerKeyUntil;
        to.TlsCertificateSha256 = from.TlsCertificateSha256;
        to.NidKeyId = from.NidKeyId;
        to.LastDeclaration = from.LastDeclaration;
        to.LastSeenAt = from.LastSeenAt;
        to.SoftwareVersion = from.SoftwareVersion;
        to.CreatedAt = from.CreatedAt;
        to.PairedAt = from.PairedAt;
        to.RevokedAt = from.RevokedAt;
        return to;
    }

    private static WorkerRecord ToRecord(ModelWorkerRow row) => new(
        row.Id, row.Name, row.Host, row.Port, row.ProxyDeclared,
        row.Placement == "local" ? WorkerPlacement.Local : WorkerPlacement.Remote, row.Safe,
        Enum.Parse<WorkerStatus>(row.Status, ignoreCase: true),
        row.WorkerPublicKey, row.WorkerKeyFingerprint, row.PreviousWorkerPublicKey, row.PreviousWorkerKeyUntil,
        row.TlsCertificateSha256, row.NidKeyId, row.LastDeclaration, row.LastSeenAt, row.SoftwareVersion,
        row.CreatedAt, row.PairedAt, row.RevokedAt, row.Version);
}
