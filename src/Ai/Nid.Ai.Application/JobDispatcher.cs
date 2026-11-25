using System.Text;
using System.Text.Json;
using Nid.Ai.Contracts;
using Nid.Ai.Domain;
using Nid.Ai.Protocol;
using Nid.Ai.Protocol.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

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
    IEnumerable<IJobFileSource> files, IEnumerable<IJobResultSink> sinks, IAssistantPeople people, INotifier notifier, IAiSettings settings,
    IClock clock) : IJobDispatcher
{
    private const int Batch = 20;

    // One pass: a worker out of reach is not tried again, and no worker is sent more than it runs at once.
    private readonly HashSet<Guid> _unreachable = [];
    private readonly Dictionary<Guid, int> _sent = [];

    public async Task RunOnceAsync(CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        foreach (var job in await jobs.RunningDueAsync(now, Batch, ct))
            await OneAsync(job, now, PollAsync, ct);
        foreach (var job in await jobs.DueAsync(now, Batch, ct))
            await OneAsync(job, now, SendAsync, ct);
    }

    /// <summary>One job's step; anything that goes wrong with it fails that job's attempt, never the pass.</summary>
    private async Task OneAsync(JobRecord job, Instant now, Func<JobRecord, Instant, CancellationToken, Task> step, CancellationToken ct)
    {
        try
        {
            await step(job, now, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            try
            {
                if (await jobs.FindAsync(job.Id, CancellationToken.None) is { Status: JobState.Queued or JobState.Running } current)
                    await RetryAsync(current with { Result = null }, JobCodes.ResultInvalid, now, CancellationToken.None);
            }
            catch (Exception)
            {
                // the next pass tries again
            }
        }
    }

    private async Task SendAsync(JobRecord job, Instant now, CancellationToken ct)
    {
        var route = await router.RouteAsync(job.Type, job.Version, ModelCallKind.Job, job.DataClass, job.TargetWorkerId, ct, await OffsiteAsync(job, ct));
        var candidates = route.Workers.Where(w => !_unreachable.Contains(w.Id) && _sent.GetValueOrDefault(w.Id) < Slots(w)).ToList();
        if (candidates.Count == 0)
        {
            var reason = route.Reason ?? (route.Workers.Any(w => !_unreachable.Contains(w.Id)) ? JobCodes.WorkerBusy : AiCodes.WorkerUnreachable);
            await jobs.TryUpdateAsync(job with { WaitingReason = reason, RunAfter = now + JobRetries.WaitWhenNowhere }, ct);
            return;
        }

        // Claimed first: another pass that read the same row loses on its version and sends nothing.
        if (!await jobs.TryUpdateAsync(job with { Status = JobState.Running, LeaseUntil = now + JobRetries.Lease, NextPollAt = now + JobRetries.Lease, WaitingReason = null }, ct))
            return;
        var claimed = (await jobs.FindAsync(job.Id, ct))!;

        try
        {
            await SendClaimedAsync(claimed, candidates, now, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopping: the job goes back to the queue as it was, rather than waiting out its lease.
            if (await jobs.FindAsync(job.Id, CancellationToken.None) is { Status: JobState.Running, WorkerJobId: null } current)
                await jobs.TryUpdateAsync(current with { Status = JobState.Queued, LeaseUntil = null, NextPollAt = null }, CancellationToken.None);
            throw;
        }
    }

    private async Task SendClaimedAsync(JobRecord claimed, IReadOnlyList<WorkerRecord> candidates, Instant now, CancellationToken ct)
    {
        if (!(await SubmissionAsync(claimed, ct)).TryGet(out var submission, out var refused))
        {
            await FinishAsync(claimed, JobState.Failed, refused!.Code, now, ct);
            return;
        }

        var busy = false;
        foreach (var worker in candidates)
        {
            if (worker.NidKeyId is not { } keyId || await keys.FindAsync(keyId, ct) is not { } key)
                continue;
            try
            {
                var sent = await client.SubmitJobAsync(worker, key, submission, ct);
                _sent[worker.Id] = _sent.GetValueOrDefault(worker.Id) + 1;
                if (await jobs.TryUpdateAsync(claimed with
                    {
                        WorkerId = worker.Id,
                        WorkerJobId = sent.WorkerJobId,
                        ResponseSecret = sent.ResponseSecret,
                        SentAt = now,
                        NextPollAt = now + JobRetries.FirstPoll,
                    }, ct))
                    await jobs.AddDispatchAsync(claimed.Id, worker.Id, now, ct);
                return;
            }
            catch (WorkerCallException e) when (e.Code.EndsWith(":input_invalid", StringComparison.Ordinal))
            {
                await FinishAsync(claimed, JobState.Failed, JobCodes.InvalidInput, now, ct);
                return;
            }
            catch (WorkerCallException e) when (e.Code == AiCodes.WorkerUnreachable)
            {
                _unreachable.Add(worker.Id);
            }
            catch (WorkerCallException)
            {
                busy = true; // refused (busy, unsupported): the next worker
            }
        }

        if (busy)
        {
            // A worker that answered but could not take it now: not the job's fault, no attempt counted.
            await jobs.TryUpdateAsync(claimed with
            {
                Status = JobState.Queued, LeaseUntil = null, NextPollAt = null, RunAfter = now + JobRetries.WaitWhenNowhere, WaitingReason = JobCodes.WorkerBusy,
            }, ct);
            return;
        }

        // No worker that should take it can be reached: an attempt, so a job does not wait forever on a dead network.
        await RetryAsync(claimed, AiCodes.WorkerUnreachable, now, ct);
    }

    private async Task PollAsync(JobRecord job, Instant now, CancellationToken ct)
    {
        if (job.LeaseUntil <= now)
        {
            await RetryAsync(job, JobCodes.LeaseExpired, now, ct);
            return;
        }

        if (job.SentAt is not null && job.ResponseSecret is null)
        {
            // Nid can no longer read the secret its result is bound to (lost key ring): sent again, no attempt counted.
            await CancelAsync(job, ct);
            if (await jobs.TryUpdateAsync(job with
                {
                    Status = JobState.Queued, LeaseUntil = null, NextPollAt = null, WorkerId = null, WorkerJobId = null, SentAt = null,
                    RunAfter = now, WaitingReason = "ai.secret_lost",
                }, ct))
                await EndedAsync(job, "ai.secret_lost", now, ct);
            return;
        }

        if (job.WorkerId is not { } workerId || job.WorkerJobId is null || await workers.FindAsync(workerId, ct) is not { } worker
            || worker.NidKeyId is not { } keyId || await keys.FindAsync(keyId, ct) is not { } key)
        {
            await RetryAsync(job, JobCodes.WorkerFailed, now, ct);
            return;
        }

        // The household withdrew what let it go there (the opt-in, the safe tag): taken back, sent again where it may go,
        // no attempt counted (docs/ai/README.md §6.1).
        if (Domain.DataPolicy.Refusal(worker.Placement, worker.Safe, job.DataClass,
                await settings.RemoteWorkersAllowedAsync(ct) && await OffsiteAsync(job, ct)) is { } refusal)
        {
            await CancelAsync(job, ct);
            if (await jobs.TryUpdateAsync(job with
                {
                    Status = JobState.Queued, LeaseUntil = null, NextPollAt = null, WorkerId = null, WorkerJobId = null, SentAt = null,
                    ResponseSecret = null, RunAfter = now, WaitingReason = refusal,
                }, ct))
                await EndedAsync(job, refusal, now, ct);
            return;
        }

        PolledJob polled;
        try
        {
            polled = await client.PollJobAsync(worker, key, job.WorkerJobId, job.ResponseSecret!, ct);
        }
        catch (WorkerCallException)
        {
            await RetryAsync(job, JobCodes.WorkerFailed, now, ct);
            return;
        }

        switch (polled.Status.Status)
        {
            case "queued" or "running":
                await jobs.TryUpdateAsync(job with
                {
                    NextPollAt = now + JobRetries.PollEvery(now - (job.SentAt ?? now)),
                    LeaseUntil = JobRetries.LeaseAfterPoll(polled.Status.Status == "queued", now, job.SentAt, job.LeaseUntil),
                }, ct);
                break;
            case "succeeded" when Read(job, polled.Result) is { } result:
                if (sinks.FirstOrDefault(s => s.Type == job.Type) is { } sink)
                    await sink.AcceptAsync(job.FileRef, result, ct);
                await FinishAsync(job with { Result = result }, JobState.Succeeded, null, now, ct);
                break;
            case "succeeded":
                await RetryAsync(job, JobCodes.ResultInvalid, now, ct);
                break;
            case "failed" when polled.Status.Error == "input_invalid":
                await FinishAsync(job, JobState.Failed, JobCodes.InvalidInput, now, ct);
                break;
            default:
                await RetryAsync(job, polled.Status.Error is { } error ? JobRetries.WorkerFailed(error) : JobCodes.WorkerFailed, now, ct);
                break;
        }
    }

    /// <summary>The opened result read through the job's contract; null when there is none or it does not read.</summary>
    private string? Read(JobRecord job, byte[]? output)
    {
        if (output is null || contracts.Find(ModelCallKind.Job, job.Type, job.Version) is not { } contract)
            return null;
        try
        {
            return contract.ReadOutput(output);
        }
        catch (ProtocolException)
        {
            return null;
        }
    }

    private async Task RetryAsync(JobRecord job, string code, Instant now, CancellationToken ct)
    {
        await CancelAsync(job, ct);
        var attempts = job.Attempts + 1;
        if (JobRetries.Backoff(attempts) is not { } backoff)
        {
            if (await FinishAsync(job with { Attempts = attempts }, JobState.Dead, code, now, ct))
                await notifier.PublishAsync(new Notice(PlatformKinds.JobFailed, new Recipients.Admins(), $"{PlatformKinds.JobFailed}:ai.{job.Type}", [],
                    new Dictionary<string, string> { ["job"] = $"ai.{job.Type}" }), ct);
            return;
        }

        if (await jobs.TryUpdateAsync(job with
            {
                Status = JobState.Queued, Attempts = attempts, Error = code, RunAfter = now + backoff, LeaseUntil = null,
                NextPollAt = null, WorkerId = null, WorkerJobId = null, ResponseSecret = null, SentAt = null,
            }, ct))
            await EndedAsync(job, code, now, ct);
    }

    /// <summary>Best effort: a copy Nid gives up on should not keep one of the worker's slots.</summary>
    private async Task CancelAsync(JobRecord job, CancellationToken ct)
    {
        if (job.WorkerId is not { } workerId || job.WorkerJobId is not { } workerJobId || _unreachable.Contains(workerId))
            return;
        try
        {
            if (await workers.FindAsync(workerId, ct) is { } worker && worker.NidKeyId is { } keyId && await keys.FindAsync(keyId, ct) is { } key)
                await client.CancelJobAsync(worker, key, workerJobId, ct);
        }
        catch (WorkerCallException)
        {
            // gone already
        }
    }

    private async Task<bool> FinishAsync(JobRecord job, JobState state, string? error, Instant now, CancellationToken ct)
    {
        if (!await jobs.TryUpdateAsync(job with
            {
                Status = state, Error = error, FinishedAt = now, ResponseSecret = null, LeaseUntil = null, NextPollAt = null, WaitingReason = null,
            }, ct))
            return false;
        await EndedAsync(job, state == JobState.Succeeded ? "succeeded" : error ?? state.ToString().ToLowerInvariant(), now, ct);
        return true;
    }

    /// <summary>#602: the end of the job's dispatch, when it had been sent (a job refused before it reached a worker has none).</summary>
    private async Task EndedAsync(JobRecord job, string outcome, Instant now, CancellationToken ct)
    {
        if (job.SentAt is not null)
            await jobs.EndDispatchAsync(job.Id, outcome, now, ct);
    }

    /// <summary>Decision M (§6.3): a job that concerns adults leaves the premises only with <c>allow</c> from each, read at each
    /// send and poll, so a change of mind takes it back.</summary>
    private Task<bool> OffsiteAsync(JobRecord job, CancellationToken ct) => OffsiteConsent.AllowedAsync(people, job.Concerned, ct);

    private static int Slots(WorkerRecord worker) => WorkerRouter.DeclarationOf(worker)?.MaxConcurrentRequests ?? 1;

    /// <summary>A job's file reference, <c>module:reference</c>, in its two parts.</summary>
    internal static (string Module, string Reference) FileOf(string fileRef)
        => fileRef.Split(':', 2) is [var module, var reference] ? (module, reference) : (fileRef, "");

    /// <summary>What the worker receives: the job's input and, read from the module that owns it, its file.</summary>
    private async Task<Result<JobSubmission>> SubmissionAsync(JobRecord job, CancellationToken ct)
    {
        var input = JsonDocument.Parse(job.Input).RootElement.Clone();
        if (job.FileRef is null)
            return new JobSubmission(job.Type, job.Version, input, null);
        var (module, reference) = FileOf(job.FileRef);
        var file = files.FirstOrDefault(f => f.Module == module) is { } source ? await source.ReadAsync(reference, ct) : null;
        return file switch
        {
            null => ModelFailures.Of(JobCodes.FileMissing),
            { Length: > JobSubmission.MaxFileBytes } => ModelFailures.Of(JobCodes.TooLarge),
            _ => new JobSubmission(job.Type, job.Version, input, Convert.ToBase64String(file)),
        };
    }
}
