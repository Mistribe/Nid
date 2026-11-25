using NodaTime;

namespace Nid.Ai.Domain;

/// <summary>A job's clock (<c>docs/ai/README.md</c> §4): its lease on a worker, how often it is polled, and how a failed attempt
/// waits 30 s, 2 min, 10 min, 1 h before the next, the job <c>dead</c> at the fifth.</summary>
public static class JobRetries
{
    public const int MaxAttempts = 5;

    /// <summary>How long a job may go without a poll that finds it on its worker: renewed by each one (<see cref="LeaseAfterPoll"/>).</summary>
    public static readonly Duration Lease = Duration.FromMinutes(10);

    /// <summary>#625: how long a job may run on a worker from its send, whatever its polls say (a long OCR on a shared CPU).</summary>
    public static readonly Duration MaxRun = Duration.FromHours(1);

    /// <summary>How long a job waits when no worker can take it now (none eligible, or all busy).</summary>
    public static readonly Duration WaitWhenNowhere = Duration.FromSeconds(30);

    /// <summary>The first poll after a send.</summary>
    public static readonly Duration FirstPoll = Duration.FromSeconds(5);

    private static readonly Duration[] Backoffs = [Duration.FromSeconds(30), Duration.FromMinutes(2), Duration.FromMinutes(10), Duration.FromHours(1)];

    /// <summary>The wait before the next attempt once <paramref name="attempts"/> have failed; null when the job is dead.</summary>
    public static Duration? Backoff(int attempts) => attempts >= MaxAttempts ? null : Backoffs[Math.Max(attempts, 1) - 1];

    /// <summary>5 seconds apart for a job's first minute on the worker, 30 seconds after that.</summary>
    public static Duration PollEvery(Duration sentFor) => sentFor < Duration.FromMinutes(1) ? FirstPoll : Duration.FromSeconds(30);

    /// <summary>The lease after a poll: renewed while the job waits in the worker's queue, which does not use it; renewed while it
    /// runs (#625) up to <see cref="MaxRun"/> after it was sent, never shortened.</summary>
    public static Instant? LeaseAfterPoll(bool queued, Instant now, Instant? sentAt, Instant? lease)
    {
        if (queued)
            return now + Lease;
        if (sentAt is not { } sent)
            return lease;
        var renewed = Instant.Min(now + Lease, sent + MaxRun);
        return lease is { } current && current > renewed ? current : renewed;
    }

    /// <summary>A worker's error as Nid keeps it: a short code, nothing else.</summary>
    public static string ErrorCode(string error)
        => new string(error.Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-').Take(64).ToArray()) is { Length: > 0 } code ? code : "unknown";

    /// <summary>#626: a worker's failure as a code Nid keeps and shows: <c>ai.worker_failed:</c> and its <see cref="ErrorCode"/>,
    /// 64 characters at most, the width of a code column (<c>llm_call.code</c>).</summary>
    public static string WorkerFailed(string error)
        => $"{Contracts.JobCodes.WorkerFailed}:{ErrorCode(error)}" is var code && code.Length > 64 ? code[..64] : code;
}
