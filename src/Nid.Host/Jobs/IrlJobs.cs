using Nid.Platform.Contracts;
using Nid.RealEstate.Application.Irl;
using Nid.RealEstate.Contracts;
using Quartz;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Host.Jobs;

public static class IrlJobs
{
    public static readonly JobKey WeeklyKey = new("irl-weekly");
}

/// <summary>
/// Sends <see cref="CollectIrlCommand"/> as the system actor, like <c>prices-daily</c> (rental spec §3). A refusal
/// (the admin has not turned IRL on) is logged as information, not as an error. Never throws.
/// </summary>
[DisallowConcurrentExecution]
public sealed partial class IrlWeeklyJob(IServiceScopeFactory scopes, ILogger<IrlWeeklyJob> logger) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            var result = await scope.ServiceProvider.GetRequiredService<IInvoker>().InvokeAsync(new CollectIrlCommand(), cancellationToken);
            if (result.TryGetFailure(out var failure))
                LogRefused(failure!.Code);
        }
        catch (OperationCanceledException)
        {
            LogInterrupted();
        }
        catch (Exception e)
        {
            LogCrashed(e);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "irl-weekly was interrupted")]
    private partial void LogInterrupted();

    [LoggerMessage(Level = LogLevel.Information, Message = "irl-weekly did not run: {Code}")]
    private partial void LogRefused(string code);

    [LoggerMessage(Level = LogLevel.Error, Message = "irl-weekly crashed")]
    private partial void LogCrashed(Exception exception);
}

public sealed class QuartzIrlJobTrigger(ISchedulerFactory schedulers) : IIrlJobTrigger
{
    public async Task<Result> TriggerAsync(CancellationToken ct)
    {
        // A scheduler shut down (or not started) is a coded refusal, never a 500.
        try
        {
            var scheduler = await schedulers.GetScheduler(ct);
            await scheduler.TriggerJob(IrlJobs.WeeklyKey, cancellationToken: ct);
            return Result.Success();
        }
        catch (SchedulerException e)
        {
            return Result.Failure(new Failure(IrlCodes.JobsDisabled, "The job scheduler cannot take the request: " + e.Message, null));
        }
    }
}
