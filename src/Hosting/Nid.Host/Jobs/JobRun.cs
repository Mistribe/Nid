using Nid.Platform.Admin;
using Quartz;

namespace Nid.Host.Jobs;

/// <summary>
/// A job's outcome, marked on its context for <see cref="JobRunListener"/> (admin jobs design §3.2). The jobs never throw, so the
/// listener cannot tell a crash or a refusal from a success on its own.
/// </summary>
internal static class JobRun
{
    public const string IdKey = "nid.run.id";
    private const string OutcomeKey = "nid.run.outcome";
    private const string SummaryKey = "nid.run.summary";
    private const int SummaryLength = 500;

    public static Guid? Id(IJobExecutionContext context) => context.MergedJobDataMap.TryGetValue(IdKey, out var id) ? id as Guid? : null;

    public static void Succeeded(IJobExecutionContext context) => Mark(context, JobOutcomes.Succeeded, null);
    public static void Skipped(IJobExecutionContext context, string code) => Mark(context, JobOutcomes.Skipped, code);
    public static void Interrupted(IJobExecutionContext context) => Mark(context, JobOutcomes.Interrupted, null);
    public static void Failed(IJobExecutionContext context, Exception e) => Mark(context, JobOutcomes.Failed, Summary(e));

    /// <summary>Type and message only: the stack trace stays in the rotated file logs.</summary>
    public static string Summary(Exception e) => Cut($"{e.GetType().Name}: {e.Message}");

    public static (string Outcome, string? Summary) Outcome(IJobExecutionContext context, JobExecutionException? thrown)
        => context.MergedJobDataMap.TryGetValue(OutcomeKey, out var outcome) && outcome is string marked
            ? (marked, context.MergedJobDataMap.TryGetValue(SummaryKey, out var summary) ? summary as string : null)
            : thrown is not null ? (JobOutcomes.Failed, Cut(thrown.Message))
            : (JobOutcomes.Succeeded, null);

    private static void Mark(IJobExecutionContext context, string outcome, string? summary)
    {
        // The firing's own copy of the job data: shared with the listeners, never persisted (Quartz 4 removed Put/Get).
        context.MergedJobDataMap[OutcomeKey] = outcome;
        context.MergedJobDataMap[SummaryKey] = summary;
    }

    private static string Cut(string text) => text.Length <= SummaryLength ? text : text[..SummaryLength];
}

/// <summary>
/// Records every run, whatever fired it (admin jobs design §3.1): a row at the start, closed with the outcome at the end, then the
/// job's runs beyond the 100 most recent are purged. Never throws: recording a run must not fail it or block the scheduler.
/// </summary>
public sealed partial class JobRunListener(IServiceScopeFactory scopes, RunLogFolder logs, ILogger<JobRunListener> logger) : IJobListener
{
    public string Name => "nid-job-runs";

    public async ValueTask JobToBeExecuted(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        context.MergedJobDataMap[JobRun.IdKey] = id;
        if (logs.Open(context.JobDetail.Key.Name, id) is { } writer)
            context.MergedJobDataMap[RunLogs.WriterKey] = writer;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            // The schedules are cron triggers; TriggerJob (startup, « Synchroniser ») fires a one-shot simple trigger.
            var trigger = context.Trigger is ICronTrigger ? JobTriggers.Scheduled : JobTriggers.OnDemand;
            await scope.ServiceProvider.GetRequiredService<IJobRunStore>().StartAsync(id, context.JobDetail.Key.Name, trigger, context.FireTimeUtc, cancellationToken);
        }
        catch (Exception e)
        {
            LogRecordingFailed(context.JobDetail.Key.Name, e);
        }
    }

    public ValueTask JobExecutionVetoed(IJobExecutionContext context, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public async ValueTask JobWasExecuted(IJobExecutionContext context, JobExecutionException? jobException, CancellationToken cancellationToken = default)
    {
        if (JobRun.Id(context) is not { } id) return;
        var key = context.JobDetail.Key.Name;
        var writer = context.MergedJobDataMap.TryGetValue(RunLogs.WriterKey, out var w) ? w as RunLogWriter : null;
        writer?.Dispose();
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IJobRunStore>();
            var (outcome, summary) = JobRun.Outcome(context, jobException);
            // Not the run's token: a run interrupted by a shutdown must still be closed.
            await store.FinishAsync(id, outcome, summary, writer?.HadLines ?? false, context.FireTimeUtc + context.JobRunTime, CancellationToken.None);
            logs.Delete(key, await store.PurgeAsync(key, JobRunsKept.Count, CancellationToken.None));
        }
        catch (Exception e)
        {
            LogRecordingFailed(key, e);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The run of {Job} could not be recorded")]
    private partial void LogRecordingFailed(string job, Exception exception);
}

/// <summary>A process that died mid-run left its runs <c>running</c>: closed as interrupted before the scheduler starts (design §2.2).</summary>
public sealed class JobRunStaleClose(IServiceScopeFactory scopes) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IJobRunStore>().CloseStaleAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
