using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Nid.Core.Contracts;
using Nid.Platform.Import;
using Npgsql;
using NodaTime;

namespace Nid.Platform.Infrastructure.Persistence;

public sealed partial class ImportStagingStore(PlatformDbContext db) : IImportStaging
{
    [JsonSerializable(typeof(Dictionary<string, Guid>))]
    private sealed partial class AdultsJson : JsonSerializerContext;

    private const string UniqueViolation = "23505";

    private static readonly NodaTime.Duration OneMicrosecond = NodaTime.Duration.FromTicks(10);

    private static readonly IReadOnlyList<string> ActiveNames = ImportStates.Active.Select(ImportStates.Name).ToList();

    public async Task ExpireAsync(Instant now, CancellationToken ct)
    {
        await db.Sessions.Where(s => ActiveNames.Contains(s.State) && s.ExpiresAt < now)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.State, ImportStates.Name(ImportState.Expired))
                .SetProperty(s => s.UpdatedAt, now), ct);

        // Self-healing: a batch added between the update above and this delete (or between any two calls of
        // this method) lands on a session that is, by then, not active — so it is removed here, on this or the
        // next sweep, rather than by trying to make the two statements atomic.
        var inactive = db.Sessions.Where(s => !ActiveNames.Contains(s.State)).Select(s => s.Id);
        await db.Batches.Where(b => inactive.Contains(b.SessionId)).ExecuteDeleteAsync(ct);
    }

    public async Task<ImportSession?> CreateAsync(
        PersonId person, IReadOnlyDictionary<string, PersonId> adults, Instant now, Instant expiresAt, CancellationToken ct)
    {
        var row = new ImportSessionRow
        {
            Id = new Guid(RandomNumberGenerator.GetBytes(16)), // 128 random bits (§11.1): not an RFC 4122 v4 UUID, but Postgres uuid stores any 16 bytes
            PersonId = person.Value,
            State = ImportStates.Name(ImportState.Open),
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = expiresAt,
            Adults = JsonSerializer.Serialize(adults.ToDictionary(a => a.Key, a => a.Value.Value), AdultsJson.Default.DictionaryStringGuid),
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
        string? reportJson, CancellationToken ct, Instant? expectedUpdatedAt = null)
    {
        var fromNames = from.Select(ImportStates.Name).ToList();
        var rows = db.Sessions.Where(s => s.Id == session && fromNames.Contains(s.State));
        if (expectedUpdatedAt is { } expected)
            rows = rows.Where(s => s.UpdatedAt == expected);

        // updated_at doubles as the row's version, so it must move on every transition even when two land on
        // the same instant (or the clock stands still): one microsecond past the previous value at least.
        var moved = await rows
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.State, ImportStates.Name(to))
                .SetProperty(s => s.UpdatedAt, s => s.UpdatedAt < now ? now : s.UpdatedAt + OneMicrosecond)
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
        JsonSerializer.Deserialize(row.Adults, AdultsJson.Default.DictionaryStringGuid)!.ToDictionary(a => a.Key, a => new PersonId(a.Value)),
        row.Report,
        row.UpdatedAt);

    private static StoredBatch ToBatch(ImportBatchRow row)
        => new(row.Section, row.BatchIndex, row.ContentHash, row.EntityCount, row.Payload);
}
