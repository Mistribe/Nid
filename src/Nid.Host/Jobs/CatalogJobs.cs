using Nid.Platform.Contracts;
using Nid.Subscriptions.Application.Catalog;
using Nid.Subscriptions.Contracts;
using Quartz;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Host.Jobs;

public static class CatalogJobs
{
    public static readonly JobKey SyncKey = new("catalog-sync");
}

/// <summary>
/// Sends <see cref="SyncCatalogCommand"/> as the system actor, like <c>fx-backfill</c> (design §4 "Trigger"): the system
/// actor is admin (1G), so the command's <c>IAdminOnly</c> lets it through. Durable and without a schedule: the commit
/// is pinned, so a periodic run could only repeat the same version. Never throws.
/// </summary>
[DisallowConcurrentExecution]
public sealed partial class CatalogSyncJob(IServiceScopeFactory scopes, ILogger<CatalogSyncJob> logger) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            var result = await scope.ServiceProvider.GetRequiredService<IInvoker>().InvokeAsync(new SyncCatalogCommand(), cancellationToken);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "catalog-sync was interrupted")]
    private partial void LogInterrupted();

    [LoggerMessage(Level = LogLevel.Information, Message = "catalog-sync did not run: {Code}")]
    private partial void LogRefused(string code);

    [LoggerMessage(Level = LogLevel.Error, Message = "catalog-sync crashed")]
    private partial void LogCrashed(Exception exception);
}

public sealed class QuartzCatalogJobTrigger(ISchedulerFactory schedulers) : ICatalogJobTrigger
{
    public async Task<Result> TriggerSyncAsync(CancellationToken ct)
    {
        var scheduler = await schedulers.GetScheduler(ct);
        await scheduler.TriggerJob(CatalogJobs.SyncKey, cancellationToken: ct);
        return Result.Success();
    }
}

/// <summary>Design §4 "Trigger": at startup, a sync runs until one has succeeded. Runs after the migrations (Program).</summary>
public sealed class CatalogStartupSync(IServiceScopeFactory scopes, ICatalogJobTrigger trigger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        if (!await scope.ServiceProvider.GetRequiredService<ICatalogRunLog>().HasSucceededAsync(cancellationToken))
            await trigger.TriggerSyncAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
