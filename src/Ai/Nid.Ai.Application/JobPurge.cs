using Microsoft.Extensions.Logging;
using NodaTime;

namespace Nid.Ai.Application;

/// <summary>Deletes finished model jobs, their input and their result with them (#483, <c>docs/ai/README.md</c> §13).</summary>
public interface IJobPurge
{
    /// <summary>One pass; the number of jobs deleted.</summary>
    Task<int> RunOnceAsync(CancellationToken ct);
}

/// <summary>
/// A succeeded job's result has been read by then; a failed or dead one stays longer, so the jobs page and the <c>job_failed</c>
/// notification still explain it. Jobs are not audited (operational state, <c>AiAuditMap</c>): the pass logs its count only.
/// </summary>
public sealed partial class JobPurge(IJobStore jobs, IClock clock, ILogger<JobPurge> logger) : IJobPurge
{
    public static readonly Duration SucceededKept = Duration.FromDays(7);
    public static readonly Duration FailedKept = Duration.FromDays(30);

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        var deleted = await jobs.PurgeFinishedAsync(now - SucceededKept, now - FailedKept, ct);
        if (deleted > 0)
            LogPurged(deleted);
        return deleted;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} finished model jobs")]
    private partial void LogPurged(int count);
}
