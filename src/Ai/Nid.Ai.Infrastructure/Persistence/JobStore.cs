using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Nid.Ai.Application;
using Npgsql;
using NodaTime;

namespace Nid.Ai.Infrastructure.Persistence;

/// <summary>The <c>ai.job</c> table. A job's response secret is encrypted by Data Protection (<c>Nid.Ai.JobSecret</c>); a
/// change is saved against the row's <c>xmin</c>, so two dispatcher passes never both send or both finish a job.</summary>
public sealed class JobStore(AiDbContext db, IDataProtectionProvider protection) : IJobStore
{
    private readonly IDataProtector _protector = protection.CreateProtector("Nid.Ai.JobSecret");

    public async Task<JobRecord> AddOrFindAsync(JobRecord job, CancellationToken ct)
    {
        // A lost race on the unique index means another one queued it: that one, or, finished meanwhile, a new one.
        // Not for a transaction already open: the violation would abort it (no producer submits inside one).
        for (var attempt = 0; ; attempt++)
        {
            if (await PendingAsync(job.Type, job.InputHash, ct) is { } existing)
                return existing;

            db.Jobs.Add(ToRow(job, new JobRow { Type = job.Type, Input = job.Input, InputHash = job.InputHash, DataClass = job.DataClass, Status = "" }));
            try
            {
                await db.SaveChangesAsync(ct);
                return (await FindAsync(job.Id, ct))!;
            }
            catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } && attempt < 3)
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    public async Task<JobRecord?> FindAsync(Guid id, CancellationToken ct)
        => await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == id, ct) is { } row ? ToRecord(row) : null;

    public async Task<IReadOnlyList<JobRecord>> DueAsync(Instant now, int limit, CancellationToken ct)
        => (await db.Jobs.AsNoTracking().Where(j => j.Status == "queued" && j.RunAfter <= now)
            .OrderBy(j => j.RunAfter).ThenBy(j => j.CreatedAt).Take(limit).ToListAsync(ct)).Select(ToRecord).ToList();

    public async Task<IReadOnlyList<JobRecord>> RunningDueAsync(Instant now, int limit, CancellationToken ct)
        => (await db.Jobs.AsNoTracking().Where(j => j.Status == "running" && (j.NextPollAt <= now || j.LeaseUntil <= now))
            .OrderBy(j => j.NextPollAt).Take(limit).ToListAsync(ct)).Select(ToRecord).ToList();

    public async Task<(IReadOnlyList<JobRecord> Items, int Total, IReadOnlyList<string> Types)> PageAsync(
        string? status, string? type, int skip, int take, CancellationToken ct)
    {
        var query = db.Jobs.AsNoTracking();
        if (status is not null)
            query = query.Where(j => j.Status == status);
        if (type is not null)
            query = query.Where(j => j.Type == type);

        var total = await query.CountAsync(ct);
        // Only the listed columns: a job's input and result (a whole document's text) are never read for a page, nor its secret.
        var rows = await query.OrderByDescending(j => j.CreatedAt).ThenByDescending(j => j.Id).Skip(skip).Take(take)
            .Select(j => new JobRow
            {
                Id = j.Id, Type = j.Type, Version = j.Version, Input = "", InputHash = "", DataClass = j.DataClass, Status = j.Status,
                Attempts = j.Attempts, WorkerId = j.WorkerId, Error = j.Error, WaitingReason = j.WaitingReason, CreatedAt = j.CreatedAt,
                FinishedAt = j.FinishedAt,
            })
            .ToListAsync(ct);
        var types = await db.Jobs.AsNoTracking().Select(j => j.Type).Distinct().OrderBy(t => t).ToListAsync(ct);
        return (rows.Select(r => ToRecord(r, withSecret: false)).ToList(), total, types);
    }

    public async Task<bool> TryUpdateAsync(JobRecord job, CancellationToken ct)
    {
        var row = await db.Jobs.SingleAsync(j => j.Id == job.Id, ct);
        ToRow(job, row);
        db.Entry(row).Property(j => j.RowVersion).OriginalValue = job.RowVersion;
        // Always written: a change that leaves every column as it is would send no UPDATE, and so check no version.
        db.Entry(row).Property(j => j.Status).IsModified = true;
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
        catch (DbUpdateException e)
        {
            // Refused for what it holds (a NUL character, a value out of range): the context forgets it, so the next
            // change in this pass is not refused with it.
            db.ChangeTracker.Clear();
            throw new JobWriteException(e);
        }
    }

    private async Task<JobRecord?> PendingAsync(string type, string inputHash, CancellationToken ct)
        => await db.Jobs.AsNoTracking()
            .SingleOrDefaultAsync(j => j.Type == type && j.InputHash == inputHash && (j.Status == "queued" || j.Status == "running"), ct)
            is { } row ? ToRecord(row) : null;

    private JobRow ToRow(JobRecord job, JobRow row)
    {
        row.Id = job.Id;
        row.Type = job.Type;
        row.Version = job.Version;
        row.Input = job.Input;
        row.InputHash = job.InputHash;
        row.FileRef = job.FileRef;
        row.DataClass = job.DataClass;
        row.TargetWorkerId = job.TargetWorkerId;
        row.Status = job.Status.ToString().ToLowerInvariant();
        row.Attempts = job.Attempts;
        row.RunAfter = job.RunAfter;
        row.NextPollAt = job.NextPollAt;
        row.LeaseUntil = job.LeaseUntil;
        row.WorkerId = job.WorkerId;
        row.WorkerJobId = job.WorkerJobId;
        row.ResponseSecret = job.ResponseSecret is null ? null : _protector.Protect(job.ResponseSecret);
        row.Result = job.Result;
        row.Error = job.Error;
        row.WaitingReason = job.WaitingReason;
        row.CreatedAt = job.CreatedAt;
        row.SentAt = job.SentAt;
        row.FinishedAt = job.FinishedAt;
        return row;
    }

    /// <summary>Null when the key ring that protected it is gone: the dispatcher then sends the job again
    /// (<c>docs/ai/README.md</c> §4.2).</summary>
    private byte[]? Unprotect(byte[]? secret)
    {
        if (secret is null)
            return null;
        try
        {
            return _protector.Unprotect(secret);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    private JobRecord ToRecord(JobRow row) => ToRecord(row, withSecret: true);

    private JobRecord ToRecord(JobRow row, bool withSecret) => new(
        row.Id, row.Type, row.Version, row.Input, row.InputHash, row.FileRef, row.DataClass, row.TargetWorkerId,
        Enum.Parse<JobState>(row.Status, ignoreCase: true), row.Attempts, row.RunAfter, row.NextPollAt, row.LeaseUntil, row.WorkerId,
        row.WorkerJobId, withSecret ? Unprotect(row.ResponseSecret) : null, row.Result, row.Error,
        row.WaitingReason, row.CreatedAt, row.SentAt, row.FinishedAt, row.RowVersion);
}
