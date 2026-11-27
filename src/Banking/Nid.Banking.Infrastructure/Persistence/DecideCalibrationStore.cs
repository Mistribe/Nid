using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nid.Banking.Application;
using Nid.Banking.Domain;
using NodaTime;

namespace Nid.Banking.Infrastructure.Persistence;

public sealed class DecideCalibrationStore(BankingDbContext db) : IDecideCalibrationStore
{
    public async Task<DecideCalibrationRun> QueueAsync(DecideKey key, Instant at, CancellationToken ct)
    {
        if (await Open(key).AsNoTracking().FirstOrDefaultAsync(ct) is { } open)
            return ToModel(open);
        var row = new DecideCalibrationRow
        {
            Id = Guid.CreateVersion7(), Implementation = key.Implementation, Version = key.Version, Model = key.Model, Digest = key.Digest,
            Status = CalibrationStatuses.Queued, QueuedAt = at,
        };
        db.DecideCalibrations.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
            return ToModel(row);
        }
        catch (DbUpdateException)
        {
            // Another « Calibrer » at the same moment: the unique index kept theirs.
            db.Entry(row).State = EntityState.Detached;
            return ToModel(await Open(key).AsNoTracking().FirstAsync(ct));
        }
    }

    public async Task<DecideCalibrationRun?> NextAsync(CancellationToken ct)
        => await db.DecideCalibrations.AsNoTracking()
            .Where(c => c.Status == CalibrationStatuses.Queued || c.Status == CalibrationStatuses.Running)
            .OrderBy(c => c.QueuedAt).ThenBy(c => c.Id).FirstOrDefaultAsync(ct) is { } row ? ToModel(row) : null;

    public async Task<DecideCalibrationRun?> LatestAsync(DecideKey key, CancellationToken ct)
        => await Of(db.DecideCalibrations.AsNoTracking(), key).OrderByDescending(c => c.QueuedAt).ThenByDescending(c => c.Id).FirstOrDefaultAsync(ct) is { } row
            ? ToModel(row) : null;

    public async Task SetStatusAsync(Guid id, string status, string? code, Instant? finishedAt, CancellationToken ct)
    {
        var row = await db.DecideCalibrations.SingleAsync(c => c.Id == id, ct);
        (row.Status, row.Code, row.FinishedAt) = (status, code, finishedAt);
        await db.SaveChangesAsync(ct);
    }

    public async Task<DecideProfile?> ProfileAsync(DecideKey key, CancellationToken ct)
    {
        var row = await db.DecideProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.Implementation == key.Implementation && p.Version == key.Version
            && p.Model == key.Model && p.Digest == key.Digest, ct);
        return row is null ? null : new DecideProfile(key, row.EvalVersion, row.Options, row.Threshold, row.ColdThreshold, row.Temperature, row.Scoreless,
            JsonSerializer.Deserialize<DecideCalibration.Results>(row.Results, JsonSerializerOptions.Web)!, row.MeasuredAt);
    }

    public async Task SaveProfileAsync(DecideProfile profile, CancellationToken ct)
    {
        var key = profile.Key;
        var row = await db.DecideProfiles.SingleOrDefaultAsync(p => p.Implementation == key.Implementation && p.Version == key.Version
            && p.Model == key.Model && p.Digest == key.Digest, ct);
        if (row is null)
            db.DecideProfiles.Add(row = new DecideProfileRow { Implementation = key.Implementation, Version = key.Version, Model = key.Model, Digest = key.Digest });
        row.EvalVersion = profile.EvalVersion;
        row.Options = profile.Options;
        row.Threshold = profile.Threshold;
        row.ColdThreshold = profile.ColdThreshold;
        row.Temperature = profile.Temperature;
        row.Scoreless = profile.Scoreless;
        row.Results = JsonSerializer.Serialize(profile.Results, JsonSerializerOptions.Web);
        row.MeasuredAt = profile.MeasuredAt;
        await db.SaveChangesAsync(ct);
    }

    private IQueryable<DecideCalibrationRow> Open(DecideKey key)
        => Of(db.DecideCalibrations, key).Where(c => c.Status == CalibrationStatuses.Queued || c.Status == CalibrationStatuses.Running);

    private static IQueryable<DecideCalibrationRow> Of(IQueryable<DecideCalibrationRow> rows, DecideKey key)
        => rows.Where(c => c.Implementation == key.Implementation && c.Version == key.Version && c.Model == key.Model && c.Digest == key.Digest);

    private static DecideCalibrationRun ToModel(DecideCalibrationRow r)
        => new(r.Id, new DecideKey(r.Implementation, r.Version, r.Model, r.Digest), r.Status, r.Code, r.QueuedAt, r.FinishedAt);
}
