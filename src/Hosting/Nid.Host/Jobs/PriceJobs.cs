using Nid.Investments.Application.Prices;
using Nid.Investments.Contracts;
using Nid.Platform.Contracts;
using Quartz;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Host.Jobs;

public static class PriceJobs
{
    public static readonly JobKey DailyKey = new("prices-daily");
}

/// <summary>
/// Sends <see cref="CollectPricesCommand"/> as the system actor, like <c>fx-daily</c> (holding prices spec §5). A refusal
/// (the admin has not turned prices on) is logged as information, not as an error. Never throws.
/// </summary>
[DisallowConcurrentExecution]
public sealed partial class PriceDailyJob(IServiceScopeFactory scopes, ILogger<PriceDailyJob> logger) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            var result = await scope.ServiceProvider.GetRequiredService<IInvoker>().InvokeAsync(new CollectPricesCommand(), cancellationToken);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "prices-daily was interrupted")]
    private partial void LogInterrupted();

    [LoggerMessage(Level = LogLevel.Information, Message = "prices-daily did not run: {Code}")]
    private partial void LogRefused(string code);

    [LoggerMessage(Level = LogLevel.Error, Message = "prices-daily crashed")]
    private partial void LogCrashed(Exception exception);
}

public sealed class QuartzPriceJobTrigger(ISchedulerFactory schedulers) : IPriceJobTrigger
{
    public async Task<Result> TriggerAsync(CancellationToken ct)
    {
        // A scheduler shut down (or not started) is a coded refusal, never a 500.
        try
        {
            var scheduler = await schedulers.GetScheduler(ct);
            await scheduler.TriggerJob(PriceJobs.DailyKey, cancellationToken: ct);
            return Result.Success();
        }
        catch (SchedulerException e)
        {
            return Result.Failure(new Failure(PriceCodes.JobsDisabled, "The job scheduler cannot take the request: " + e.Message, null));
        }
    }
}
