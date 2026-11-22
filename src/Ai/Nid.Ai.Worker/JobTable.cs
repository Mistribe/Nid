using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Nid.Ai.Protocol.Framing;
using Nid.Ai.Protocol.Sealing;

namespace Nid.Ai.Worker;

/// <summary>The worker's jobs, in memory only (the worker keeps no state about the household): at most
/// <see cref="MaxJobs"/> waiting or running, <see cref="MaxConcurrent"/> running at once, a result kept
/// <see cref="ResultLifetime"/> after it finished and then forgotten. A restart forgets every job: Nid sends it again.
/// A result is sealed under its submission's context, so only the Nid that submitted it opens it.</summary>
public sealed class JobTable(IEnumerable<IJobHandler> handlers, IEnumerable<IRequestHandler> requests, TimeProvider time) : IDisposable
{
    public const int MaxJobs = 32;
    public const int MaxConcurrent = 4;

    /// <summary>Results kept, all together: past it, the worker takes no new job until some are fetched or expire.</summary>
    public const long MaxResultBytes = 256L * 1024 * 1024;
    public static readonly TimeSpan ResultLifetime = TimeSpan.FromMinutes(15);

    // The last registered handler of a type wins: a later registration replaces a built-in one.
    private readonly Dictionary<(string, int), IJobHandler> _handlers = handlers.GroupBy(h => (h.Type, h.Version)).ToDictionary(g => g.Key, g => g.Last());
    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _slots = new(MaxConcurrent);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _admission = new();
    private int _requests;

    public IEnumerable<(string Type, int Version)> Types => _handlers.Keys;

    public IEnumerable<(string Type, int Version)> RequestTypes => requests.Select(r => (r.Type, r.Version)).Distinct();

    /// <summary>Jobs running and requests being answered now.</summary>
    public int Load => _jobs.Values.Count(j => j.Status == "running") + Volatile.Read(ref _requests);

    /// <summary>Accepts a job, or answers why not: <c>unsupported</c> or <c>busy</c>.</summary>
    public (string? Id, string? Refusal) Submit(string type, int version, JsonElement input, byte[]? file, ResponseSecret submission)
    {
        Sweep();
        if (!_handlers.TryGetValue((type, version), out var handler))
            return (null, "unsupported");

        Job job;
        lock (_admission)
        {
            if (_jobs.Values.Count(j => j.Status is "queued" or "running") >= MaxJobs
                || _jobs.Values.Sum(j => (long)(j.Result?.Length ?? 0)) >= MaxResultBytes)
                return (null, "busy");

            job = new Job(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), submission, CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token));
            _jobs[job.Id] = job;
        }

        _ = Task.Run(() => RunAsync(job, handler, input.Clone(), file));
        return (job.Id, null);
    }

    public Job? Find(string id)
    {
        Sweep();
        return _jobs.GetValueOrDefault(id);
    }

    public void Cancel(string id)
    {
        if (_jobs.TryGetValue(id, out var job) && job.Status is "queued" or "running")
        {
            try
            {
                job.Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // it finished meanwhile
            }
        }
    }

    /// <summary>Counts a request being answered, for the declared load.</summary>
    public IDisposable Answering()
    {
        Interlocked.Increment(ref _requests);
        return new Release(() => Interlocked.Decrement(ref _requests));
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
        _slots.Dispose();
    }

    private async Task RunAsync(Job job, IJobHandler handler, JsonElement input, byte[]? file)
    {
        var ct = job.Cancellation.Token;
        try
        {
            await _slots.WaitAsync(ct);
            try
            {
                job.Status = "running";
                var output = await handler.RunAsync(input, file, ct);
                // The output in data frames, then an empty end: a frame holds 64 KiB at most.
                using var sealedResult = new MemoryStream();
                var writer = new ResponseWriter(job.Submission, sealedResult);
                for (var at = 0; at < output.Length; at += Protocol.Contracts.JobStatus.ResultChunk)
                    await writer.WriteAsync(FrameType.Data, output.AsMemory(at, Math.Min(Protocol.Contracts.JobStatus.ResultChunk, output.Length - at)), ct);
                await writer.WriteAsync(FrameType.End, ReadOnlyMemory<byte>.Empty, ct);
                job.Result = sealedResult.ToArray();
                Finish(job, "succeeded", null);
            }
            finally
            {
                _slots.Release();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Finish(job, "failed", "cancelled");
        }
        catch (InputInvalidException)
        {
            Finish(job, "failed", "input_invalid");
        }
        catch (Exception)
        {
            // The handler's exception says nothing Nid should store: only that it failed.
            Finish(job, "failed", "handler_failed");
        }
    }

    private void Finish(Job job, string status, string? error)
    {
        job.Error = error;
        job.FinishedAt = time.GetUtcNow();
        job.Status = status;
        job.Cancellation.Dispose();
    }

    private void Sweep()
    {
        var before = time.GetUtcNow() - ResultLifetime;
        foreach (var (id, job) in _jobs)
            if (job.FinishedAt is { } finished && finished <= before)
                _jobs.TryRemove(id, out _);
    }

    public sealed class Job(string id, ResponseSecret submission, CancellationTokenSource cancellation)
    {
        public string Id { get; } = id;

        public ResponseSecret Submission { get; } = submission;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public volatile string Status = "queued";

        public string? Error { get; set; }

        public byte[]? Result { get; set; }

        public DateTimeOffset? FinishedAt { get; set; }
    }

    private sealed class Release(Action release) : IDisposable
    {
        public void Dispose() => release();
    }
}
