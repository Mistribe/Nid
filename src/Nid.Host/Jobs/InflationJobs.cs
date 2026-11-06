using Nid.Platform.Contracts;
using Nid.Core.Application.Inflation;
using Nid.Core.Contracts;
using Nid.Core.Domain.Inflation;
using Quartz;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Host.Jobs;

public static class InflationJobs
{
    public static readonly JobKey WeeklyKey = new("cpi-weekly");
}

/// <summary>
/// Sends <see cref="CollectCpiCommand"/> as the system actor, like <c>irl-weekly</c>. A refusal
/// (the admin has not turned the CPI collection on) is logged as information, not as an error. Never throws.
/// </summary>
[DisallowConcurrentExecution]
public sealed partial class CpiWeeklyJob(IServiceScopeFactory scopes, ILogger<CpiWeeklyJob> logger) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            var result = await scope.ServiceProvider.GetRequiredService<IInvoker>().InvokeAsync(new CollectCpiCommand(), cancellationToken);
            if (result.TryGetFailure(out var failure))
            {
                LogRefused(failure!.Code);
                JobRun.Skipped(context, failure.Code);
            }
            else
                await JobIncidents.SucceededAsync(scopes, context, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogInterrupted();
            JobRun.Interrupted(context);
        }
        catch (Exception e)
        {
            LogCrashed(e);
            await JobIncidents.CrashedAsync(scopes, context, e, cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "cpi-weekly was interrupted")]
    private partial void LogInterrupted();

    [LoggerMessage(Level = LogLevel.Information, Message = "cpi-weekly did not run: {Code}")]
    private partial void LogRefused(string code);

    [LoggerMessage(Level = LogLevel.Error, Message = "cpi-weekly crashed")]
    private partial void LogCrashed(Exception exception);
}

public sealed class QuartzCpiJobTrigger(ISchedulerFactory schedulers) : ICpiJobTrigger
{
    public async Task<Result> TriggerAsync(CancellationToken ct)
    {
        // A scheduler shut down (or not started) is a coded refusal, never a 500.
        try
        {
            var scheduler = await schedulers.GetScheduler(ct);
            await scheduler.TriggerJob(InflationJobs.WeeklyKey, cancellationToken: ct);
            return Result.Success();
        }
        catch (SchedulerException e)
        {
            return Result.Failure(new Failure(InflationCodes.JobsDisabled, "The job scheduler cannot take the request: " + e.Message, null));
        }
    }
}

/// <summary>
/// At startup, a collection runs while the series is empty: the weekly cron alone would leave it empty for up to a week
/// after the deploy. The job itself refuses when the household turned the collection off. Runs after the migrations (Program).
/// </summary>
public sealed class CpiStartupCollect(IServiceScopeFactory scopes, ICpiJobTrigger trigger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        if ((await scope.ServiceProvider.GetRequiredService<ICpiStore>().ListAsync(cancellationToken)).Count == 0)
            await trigger.TriggerAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
