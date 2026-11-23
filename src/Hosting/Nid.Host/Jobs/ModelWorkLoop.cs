using Nid.Ai.Application;
using Nid.Platform.Contracts;

namespace Nid.Host.Jobs;

/// <summary>
/// Moves model jobs every 5 seconds, reads every worker's declaration every minute (<c>docs/ai/README.md</c> §4) and deletes
/// finished jobs every hour (#483). A loop of its own rather than Quartz jobs: the job runs journal and Quartz's store would write a row every 5 seconds for what
/// usually finds nothing to do. Each pass runs as the system actor; a pass that fails is logged and the next one runs.
/// </summary>
public sealed partial class ModelWorkLoop(IServiceScopeFactory scopes, ILogger<ModelWorkLoop> logger) : BackgroundService
{
    public static readonly TimeSpan JobsEvery = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan WorkersEvery = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan PurgeEvery = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(JobsEvery);
        var nextWorkers = DateTimeOffset.MinValue;
        var nextPurge = DateTimeOffset.MinValue;
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (DateTimeOffset.UtcNow >= nextWorkers)
            {
                nextWorkers = DateTimeOffset.UtcNow + WorkersEvery;
                await PassAsync<IWorkerMonitor>((m, ct) => m.RunOnceAsync(ct), stoppingToken);
                await PassAsync<IBudgetSweep>((s, ct) => s.RunOnceAsync(ct), stoppingToken); // #488: holds no answer settled
            }

            await PassAsync<IJobDispatcher>((d, ct) => d.RunOnceAsync(ct), stoppingToken);

            if (DateTimeOffset.UtcNow >= nextPurge)
            {
                nextPurge = DateTimeOffset.UtcNow + PurgeEvery;
                await PassAsync<IJobPurge>((p, ct) => p.RunOnceAsync(ct), stoppingToken);
                await PassAsync<IConversationPurge>((p, ct) => p.RunOnceAsync(ct), stoppingToken); // #531: past their retention
            }
        }
    }

    private async Task PassAsync<T>(Func<T, CancellationToken, Task> pass, CancellationToken ct) where T : notnull
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            await pass(scope.ServiceProvider.GetRequiredService<T>(), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // stopping
        }
        catch (Exception e)
        {
            LogFailed(typeof(T).Name, e);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "A model work pass failed ({Pass})")]
    private partial void LogFailed(string pass, Exception exception);
}
