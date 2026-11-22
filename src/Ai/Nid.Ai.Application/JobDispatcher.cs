using System.Text;
using System.Text.Json;
using Nid.Ai.Contracts;
using Nid.Ai.Domain;
using Nid.Ai.Protocol;
using Nid.Ai.Protocol.Contracts;
using Nid.Ai.Protocol.Framing;
using Nid.Ai.Protocol.Sealing;
using Nid.Platform.Contracts;
using NodaTime;

namespace Nid.Ai.Application;

public interface IJobDispatcher
{
    /// <summary>One pass: send the jobs whose time has come, poll the running ones, take back those whose lease ran out.</summary>
    Task RunOnceAsync(CancellationToken ct);
}

public interface IWorkerMonitor
{
    /// <summary>One pass: read every active worker's declaration (its load, what it serves, the Nid key it pinned).</summary>
    Task RunOnceAsync(CancellationToken ct);
}

/// <summary>Reads a worker's declaration with Nid's newest key first (it works once the operator confirmed it on the
/// worker's console), then the one the worker pinned; moves the worker to the newest key once it declares it.</summary>
public sealed class WorkerRefresher(IWorkerStore store, IWorkerClient client, INidKeys keys, IClock clock)
{
    /// <summary>The refreshed worker, or the refusal code.</summary>
    public async Task<(WorkerRecord? Worker, string? Refusal)> RefreshAsync(WorkerRecord worker, CancellationToken ct)
    {
        if (WorkerRules.Call(worker.Status) is { } refusal)
            return (null, refusal);

        var active = await keys.ActiveAsync(ct);
        var pinned = worker.NidKeyId is { } id ? await keys.FindAsync(id, ct) : null;
        WorkerDeclaration? declaration = null;
        NidKey? used = null;
        string? failure = null;
        foreach (var key in new[] { active, pinned }.OfType<NidKey>().DistinctBy(k => k.Id))
        {
            try
            {
                declaration = await client.DeclarationAsync(worker, key, ct);
                used = key;
                break;
            }
            catch (WorkerCallException e)
            {
                failure = e.Code;
            }
        }

        if (declaration is null || used is null)
            return (null, failure ?? AiCodes.WorkerRefused);

        var refreshed = worker with
        {
            NidKeyId = declaration.NidKeyFingerprint == used.Fingerprint ? used.Id : worker.NidKeyId,
            LastDeclaration = JsonSerializer.Serialize(declaration, ContractJson.Strict),
            SoftwareVersion = declaration.SoftwareVersion,
            LastSeenAt = clock.GetCurrentInstant(),
        };
        if (!await store.UpdateAsync(refreshed, ct))
            return (null, AiCodes.WorkerChanged);
        await keys.RetireUnusedAsync(ct);
        return ((await store.FindAsync(worker.Id, ct))!, null);
    }
}

public sealed class WorkerMonitor(IWorkerStore store, WorkerRefresher refresher) : IWorkerMonitor
{
    public async Task RunOnceAsync(CancellationToken ct)
    {
        // A worker that does not answer keeps its last declaration; the router stops choosing it three minutes after it was
        // last seen.
        foreach (var worker in (await store.ListAsync(ct)).Where(w => w.Status == WorkerStatus.Active))
            await refresher.RefreshAsync(worker, ct);
    }
}

/// <summary>Moves jobs through their states (<c>docs/architecture.md</c> §9.3, <c>docs/ai/README.md</c> §4): sends each to the
/// best eligible worker (the next one when a worker refuses or cannot be reached), polls it 5 seconds apart for its first
/// minute and 30 seconds apart after that, opens its result with the secret of its submission, reads it through its
/// contract. A failure, an unreadable result or a lease run out counts an attempt: the job waits 30 s, 2 min, 10 min, 1 h,
/// and is <c>dead</c> at the fifth, the admins told. Every change is saved against the row's version: two passes at once
/// never both send or both finish a job.</summary>
public sealed class JobDispatcher(
    IJobStore jobs, IWorkerStore workers, IWorkerRouter router, IWorkerClient client, INidKeys keys, ModelContracts contracts,
    IEnumerable<IJobFileSource> files, INotifier notifier, IClock clock) : IJobDispatcher
{
    public const int MaxAttempts = 5;
    public static readonly Duration Lease = Duration.FromMinutes(10);
    public static readonly Duration WaitWhenNowhere = Duration.FromSeconds(30);
    private static readonly Duration[] Backoff = [Duration.FromSeconds(30), Duration.FromMinutes(2), Duration.FromMinutes(10), Duration.FromHours(1)];
    private const int Batch = 20;

    public async Task RunOnceAsync(CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        foreach (var job in await jobs.RunningDueAsync(now, Batch, ct))
            await PollAsync(job, now, ct);
        foreach (var job in await jobs.DueAsync(now, Batch, ct))
            await SendAsync(job, now, ct);
    }

    private async Task SendAsync(JobRecord job, Instant now, CancellationToken ct)
    {
        var route = await router.RouteAsync(job.Type, job.Version, ModelCallKind.Job, job.DataClass, job.TargetWorkerId, ct);
        if (route.Workers.Count == 0)
        {
            await jobs.TryUpdateAsync(job with { WaitingReason = route.Reason, RunAfter = now + WaitWhenNowhere }, ct);
            return;
        }

        // Claimed first: another pass that read the same row loses on its version and sends nothing.
        if (!await jobs.TryUpdateAsync(job with { Status = JobState.Running, LeaseUntil = now + Lease, NextPollAt = now + Lease, WaitingReason = null }, ct))
            return;
        var claimed = (await jobs.FindAsync(job.Id, ct))!;

        JobSubmission submission;
        try
        {
            submission = new JobSubmission(job.Type, job.Version, JsonDocument.Parse(job.Input).RootElement.Clone(), await FileAsync(job, ct));
        }
        catch (ModelCallException e)
        {
            await FinishAsync(claimed, JobState.Failed, e.Code, now, ct);
            return;
        }

        foreach (var worker in route.Workers)
        {
            if (worker.NidKeyId is not { } keyId || await keys.FindAsync(keyId, ct) is not { } key)
                continue;
            try
            {
                var sent = await client.SubmitJobAsync(worker, key, submission, ct);
                await jobs.TryUpdateAsync(claimed with
                {
                    WorkerId = worker.Id,
                    WorkerJobId = sent.WorkerJobId,
                    ResponseSecret = sent.ResponseSecret,
                    SentAt = now,
                    NextPollAt = now + Duration.FromSeconds(5),
                }, ct);
                return;
            }
            catch (WorkerCallException)
            {
                // the next worker
            }
        }

        // Every worker refused or was out of reach: not the job's fault, no attempt counted.
        await jobs.TryUpdateAsync(claimed with
        {
            Status = JobState.Queued, LeaseUntil = null, NextPollAt = null, RunAfter = now + WaitWhenNowhere, WaitingReason = JobCodes.WorkerFailed,
        }, ct);
    }

    private async Task PollAsync(JobRecord job, Instant now, CancellationToken ct)
    {
        if (job.LeaseUntil <= now)
        {
            await RetryAsync(job, JobCodes.LeaseExpired, now, ct);
            return;
        }

        if (job.WorkerId is not { } workerId || job.WorkerJobId is null || await workers.FindAsync(workerId, ct) is not { } worker
            || worker.NidKeyId is not { } keyId || await keys.FindAsync(keyId, ct) is not { } key)
        {
            await RetryAsync(job, JobCodes.WorkerFailed, now, ct);
            return;
        }

        PolledJob polled;
        try
        {
            polled = await client.PollJobAsync(worker, key, job.WorkerJobId, ct);
        }
        catch (WorkerCallException)
        {
            await RetryAsync(job, JobCodes.WorkerFailed, now, ct);
            return;
        }

        switch (polled.Status.Status)
        {
            case "queued" or "running":
                var sentFor = now - (job.SentAt ?? now);
                await jobs.TryUpdateAsync(job with { NextPollAt = now + (sentFor < Duration.FromMinutes(1) ? Duration.FromSeconds(5) : Duration.FromSeconds(30)) }, ct);
                break;
            case "succeeded" when Open(job, polled.SealedResult!) is { } result:
                await FinishAsync(job with { Result = result }, JobState.Succeeded, null, now, ct);
                break;
            case "succeeded":
                await RetryAsync(job, JobCodes.ResultInvalid, now, ct);
                break;
            case "failed" when polled.Status.Error == "input_invalid":
                await FinishAsync(job, JobState.Failed, JobCodes.InvalidInput, now, ct);
                break;
            default:
                await RetryAsync(job, polled.Status.Error is { } error ? $"{JobCodes.WorkerFailed}:{error}" : JobCodes.WorkerFailed, now, ct);
                break;
        }
    }

    /// <summary>The result, opened with the submission's secret and read through the job's contract; null when either fails.</summary>
    private string? Open(JobRecord job, byte[] sealedResult)
    {
        if (job.ResponseSecret is null || contracts.Find(ModelCallKind.Job, job.Type, job.Version) is not { } contract)
            return null;
        try
        {
            var output = new MemoryStream();
            var frames = ResponseReader.ReadAsync(ResponseSecret.FromBytes(job.ResponseSecret), new MemoryStream(sealedResult),
                new FrameLimits(64 * 1024, JobStatus.MaxResultBytes / JobStatus.ResultChunk + 2, JobStatus.MaxResultBytes), CancellationToken.None);
            var ended = false;
            foreach (var frame in frames.ToBlockingEnumerable())
            {
                if (frame.Type == FrameType.Data)
                    output.Write(frame.Content.Span);
                ended = frame.Type == FrameType.End;
            }

            return ended ? contract.ReadOutput(output.ToArray()) : null;
        }
        catch (ProtocolException)
        {
            return null;
        }
    }

    private async Task RetryAsync(JobRecord job, string code, Instant now, CancellationToken ct)
    {
        var attempts = job.Attempts + 1;
        if (attempts >= MaxAttempts)
        {
            if (await FinishAsync(job with { Attempts = attempts }, JobState.Dead, code, now, ct))
                await notifier.PublishAsync(new Notice("job_failed", new Recipients.Admins(), $"job_failed:ai.{job.Type}", [],
                    new Dictionary<string, string> { ["job"] = $"ai.{job.Type}" }), ct);
            return;
        }

        await jobs.TryUpdateAsync(job with
        {
            Status = JobState.Queued, Attempts = attempts, Error = code, RunAfter = now + Backoff[attempts - 1], LeaseUntil = null,
            NextPollAt = null, WorkerId = null, WorkerJobId = null, ResponseSecret = null, SentAt = null,
        }, ct);
    }

    private Task<bool> FinishAsync(JobRecord job, JobState state, string? error, Instant now, CancellationToken ct)
        => jobs.TryUpdateAsync(job with
        {
            Status = state, Error = error, FinishedAt = now, ResponseSecret = null, LeaseUntil = null, NextPollAt = null, WaitingReason = null,
        }, ct);

    private async Task<string?> FileAsync(JobRecord job, CancellationToken ct)
    {
        if (job.FileRef is null)
            return null;
        var (module, reference) = job.FileRef.Split(':', 2) is [var m, var r] ? (m, r) : (job.FileRef, "");
        var file = files.FirstOrDefault(f => f.Module == module) is { } source ? await source.ReadAsync(reference, ct) : null;
        return file switch
        {
            null => throw new ModelCallException("ai.file_missing"),
            { Length: > JobSubmission.MaxFileBytes } => throw new ModelCallException(JobCodes.TooLarge),
            _ => Convert.ToBase64String(file),
        };
    }
}
