using Nid.Banking.Application.Reconciliation;
using Nid.Platform.Contracts;
using Quartz;

namespace Nid.Host.Jobs;

public static class CategoryBackfillJobs
{
    public static readonly JobKey BackfillKey = new("bank-category-backfill");
}

/// <summary>Runs <see cref="CategoryBackfill"/> as the system actor. Durable, no trigger of its own: fired at startup, harmless when re-run. Never throws.</summary>
[DisallowConcurrentExecution]
public sealed partial class CategoryBackfillJob(IServiceScopeFactory scopes, ILogger<CategoryBackfillJob> logger) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            LogDone(await scope.ServiceProvider.GetRequiredService<CategoryBackfill>().RunAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogInterrupted();
        }
        catch (Exception e)
        {
            LogCrashed(e);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "bank-category-backfill categorised {Lines} lines")]
    private partial void LogDone(int lines);

    [LoggerMessage(Level = LogLevel.Information, Message = "bank-category-backfill was interrupted")]
    private partial void LogInterrupted();

    [LoggerMessage(Level = LogLevel.Error, Message = "bank-category-backfill crashed")]
    private partial void LogCrashed(Exception exception);
}

/// <summary>At startup, fires the backfill once; a scheduler that refuses is only logged, startup goes on.</summary>
public sealed partial class CategoryBackfillStartup(ISchedulerFactory schedulers, ILogger<CategoryBackfillStartup> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await (await schedulers.GetScheduler(cancellationToken)).TriggerJob(CategoryBackfillJobs.BackfillKey, cancellationToken: cancellationToken);
        }
        catch (SchedulerException e)
        {
            LogRefused(e);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "bank-category-backfill was not started")]
    private partial void LogRefused(Exception exception);
}
