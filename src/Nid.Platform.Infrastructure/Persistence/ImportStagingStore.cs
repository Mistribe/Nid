using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Platform.Import;
using Npgsql;
using NodaTime;

namespace Nid.Platform.Infrastructure.Persistence;

public sealed class ImportStagingStore(PlatformDbContext db) : IImportStaging
{
    private const string UniqueViolation = "23505";

    private static readonly IReadOnlyList<string> ActiveNames = ImportStates.Active.Select(ImportStates.Name).ToList();

    public async Task ExpireAsync(Instant now, CancellationToken ct)
    {
        var expired = db.Sessions.Where(s => ActiveNames.Contains(s.State) && s.ExpiresAt < now).Select(s => s.Id);
        await db.Batches.Where(b => expired.Contains(b.SessionId)).ExecuteDeleteAsync(ct);
        await db.Sessions.Where(s => ActiveNames.Contains(s.State) && s.ExpiresAt < now)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.State, ImportStates.Name(ImportState.Expired))
                .SetProperty(s => s.UpdatedAt, now), ct);
    }

    public async Task<ImportSession?> CreateAsync(
        PersonId person, IReadOnlyDictionary<string, PersonId> adults, Instant now, Instant expiresAt, CancellationToken ct)
    {
        var row = new ImportSessionRow
        {
            Id = Guid.NewGuid(), // 122 random bits: the id is unguessable (§11.1), unlike a time-ordered v7
            PersonId = person.Value,
            State = ImportStates.Name(ImportState.Open),
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = expiresAt,
            Adults = JsonSerializer.Serialize(adults.ToDictionary(a => a.Key, a => a.Value.Value)),
        };
        db.Sessions.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            db.Entry(row).State = EntityState.Detached;
            return null;
        }

        return ToSession(row);
    }

    public async Task<ImportSession?> FindOwnedAsync(Guid id, PersonId person, CancellationToken ct)
    {
        var row = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id && s.PersonId == person.Value, ct);
        return row is null ? null : ToSession(row);
    }

    public async Task<IReadOnlyList<ImportSession>> ListOwnedAsync(PersonId person, int limit, CancellationToken ct)
        => (await db.Sessions.AsNoTracking()
                .Where(s => s.PersonId == person.Value)
                .OrderByDescending(s => s.CreatedAt)
                .Take(limit)
                .ToListAsync(ct))
            .Select(ToSession).ToList();

    public async Task<StoredBatch?> FindBatchAsync(Guid session, string section, int index, CancellationToken ct)
    {
        var row = await db.Batches.AsNoTracking()
            .SingleOrDefaultAsync(b => b.SessionId == session && b.Section == section && b.BatchIndex == index, ct);
        return row is null ? null : ToBatch(row);
    }

    public async Task<IReadOnlyList<StoredBatch>> BatchesAsync(Guid session, CancellationToken ct)
        => (await db.Batches.AsNoTracking()
                .Where(b => b.SessionId == session)
                .OrderBy(b => b.Section).ThenBy(b => b.BatchIndex)
                .ToListAsync(ct))
            .Select(ToBatch).ToList();

    public async Task<int> EntityCountAsync(Guid session, CancellationToken ct)
        => await db.Batches.Where(b => b.SessionId == session).SumAsync(b => b.EntityCount, ct);

    public async Task<bool> AddBatchAsync(Guid session, StoredBatch batch, Instant now, CancellationToken ct)
    {
        var row = new ImportBatchRow
        {
            SessionId = session,
            Section = batch.Section,
            BatchIndex = batch.Index,
            ContentHash = batch.Hash,
            EntityCount = batch.Count,
            Payload = batch.Payload,
            ReceivedAt = now,
        };
        db.Batches.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
            // Detached once saved: a later insert into the same (session, section, index) slot on this same
            // context must reach the database and fail there, not collide with this row in the change tracker.
            db.Entry(row).State = EntityState.Detached;
            return true;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            db.Entry(row).State = EntityState.Detached;
            return false;
        }
    }

    public async Task<bool> TransitionAsync(
        Guid session, IReadOnlyCollection<ImportState> from, ImportState to, Instant now, Instant expiresAt,
        string? reportJson, CancellationToken ct)
    {
        var fromNames = from.Select(ImportStates.Name).ToList();
        var moved = await db.Sessions
            .Where(s => s.Id == session && fromNames.Contains(s.State))
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.State, ImportStates.Name(to))
                .SetProperty(s => s.UpdatedAt, now)
                .SetProperty(s => s.ExpiresAt, expiresAt)
                .SetProperty(s => s.Report, reportJson), ct);
        return moved == 1;
    }

    public async Task ClearBatchesAsync(Guid session, CancellationToken ct)
        => await db.Batches.Where(b => b.SessionId == session).ExecuteDeleteAsync(ct);

    private static ImportSession ToSession(ImportSessionRow row) => new(
        row.Id,
        new PersonId(row.PersonId),
        ImportStates.Parse(row.State),
        row.CreatedAt,
        row.ExpiresAt,
        JsonSerializer.Deserialize<Dictionary<string, Guid>>(row.Adults)!.ToDictionary(a => a.Key, a => new PersonId(a.Value)),
        row.Report);

    private static StoredBatch ToBatch(ImportBatchRow row)
        => new(row.Section, row.BatchIndex, row.ContentHash, row.EntityCount, row.Payload);
}
