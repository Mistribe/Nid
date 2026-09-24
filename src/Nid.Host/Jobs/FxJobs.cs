using Microsoft.Extensions.DependencyInjection.Extensions;
using Nid.Core.Application.Fx;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using Npgsql;
using Quartz;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Host.Jobs;

public static class FxJobs
{
    public static readonly JobKey DailyKey = new("fx-daily");
    public static readonly JobKey BackfillKey = new("fx-backfill");
}

/// <summary>
/// A job only sends a Synapse command as the system actor (design §6.2), so audit and context go through the
/// pipeline like any other entry point. It never throws: the collector journals every failure, and a shutdown's
/// cancellation (the scheduler interrupts running jobs, <see cref="JobsSetup"/>) closes the run as interrupted.
/// </summary>
public abstract partial class FxJob(IServiceScopeFactory scopes, ILogger logger) : IJob
{
    protected abstract IRequest<FxSyncRunView> Command { get; }

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            var invoker = scope.ServiceProvider.GetRequiredService<IInvoker>();
            var result = await invoker.InvokeAsync(Command, cancellationToken);
            if (result.TryGetFailure(out var failure))
                LogRefused(GetType().Name, failure!.Code);
        }
        catch (OperationCanceledException)
        {
            LogInterrupted(GetType().Name);
        }
        catch (Exception e)
        {
            LogCrashed(GetType().Name, e);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Job} was interrupted")]
    private partial void LogInterrupted(string job);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Job} did not run: {Code}")]
    private partial void LogRefused(string job, string code);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Job} crashed")]
    private partial void LogCrashed(string job, Exception exception);
}

[DisallowConcurrentExecution]
public sealed class FxDailyJob(IServiceScopeFactory scopes, ILogger<FxDailyJob> logger) : FxJob(scopes, logger)
{
    protected override IRequest<FxSyncRunView> Command => new CollectDailyFxRatesCommand();
}

[DisallowConcurrentExecution]
public sealed class FxBackfillJob(IServiceScopeFactory scopes, ILogger<FxBackfillJob> logger) : FxJob(scopes, logger)
{
    protected override IRequest<FxSyncRunView> Command => new BackfillFxRatesCommand();
}

public sealed class QuartzFxJobTrigger(ISchedulerFactory schedulers) : IFxJobTrigger
{
    public async Task<Result> TriggerBackfillAsync(CancellationToken ct)
    {
        var scheduler = await schedulers.GetScheduler(ct);
        await scheduler.TriggerJob(FxJobs.BackfillKey, cancellationToken: ct);
        return Result.Success();
    }
}

/// <summary>
/// Design §6.2: at startup, the backfill runs until one has succeeded. Not "while the rate table is empty": a failed
/// or interrupted ECB fetch leaves the daily job to fill SubTracker, and the history would never be fetched again.
/// Runs after the migrations (Program).
/// </summary>
public sealed class FxStartupBackfill(IServiceScopeFactory scopes, IFxJobTrigger trigger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        if (!await scope.ServiceProvider.GetRequiredService<IFxRunLog>().HasSucceededAsync(FxRunKind.Backfill, cancellationToken))
            await trigger.TriggerBackfillAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class JobsSetup
{
    /// <summary>
    /// Quartz in the process (D8) on a persistent PostgreSQL store in the platform schema. The tables come from the
    /// Platform migration; Quartz only validates them. No clustering: one nid-api instance runs (architecture §10),
    /// and a second instance would be the moment to turn it on, with its own test.
    /// </summary>
    public static IServiceCollection AddNidJobs(this IServiceCollection services, string connectionString, FxOptions options, string schedulerName)
    {
        services.AddQuartz(schedulerName, q =>
        {
            q.UsePersistentStore(store =>
            {
                store.UsePostgres(NpgsqlFactory.Instance, connectionString);
                store.UseSystemTextJsonSerializer();
                store.ConfigureStore(o =>
                {
                    o.TablePrefix = "platform.qrtz_";
                    o.SchemaProvisioning = SchemaProvisioning.Validate;
                });
            });

            q.AddJob<FxDailyJob>(job => job.WithIdentity(FxJobs.DailyKey));
            q.AddTrigger(t => t
                .ForJob(FxJobs.DailyKey)
                .WithIdentity("fx-daily")
                .WithCronSchedule(options.DailyCron, cron => cron.InTimeZone(TimeZoneInfo.Utc)));

            // Durable: it has no trigger of its own, it is fired on demand and at startup.
            q.AddJob<FxBackfillJob>(job => job.WithIdentity(FxJobs.BackfillKey).StoreDurably());

            // A shutdown cancels a running collection, then waits for it: the collector closes the run as
            // interrupted instead of leaving it "running" for an hour.
            q.ConfigureScheduler(o => o.ShutdownJobInterruption = ShutdownJobInterruption.WhenWaitingForJobs);
        });
        services.AddQuartzHostedService(schedulerName, o => o.WaitForJobsToComplete = true);

        // A named scheduler's parts are keyed services: there is no unkeyed ISchedulerFactory to inject.
        services.Replace(ServiceDescriptor.Singleton<IFxJobTrigger>(sp =>
            new QuartzFxJobTrigger(sp.GetRequiredKeyedService<ISchedulerFactory>(schedulerName))));
        services.AddHostedService<FxStartupBackfill>();
        return services;
    }
}
