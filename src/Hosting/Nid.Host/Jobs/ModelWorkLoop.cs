using Nid.Ai.Application;
using Nid.Platform.Contracts;

namespace Nid.Host.Jobs;

/// <summary>
/// Moves model jobs every 5 seconds, reads every worker's declaration every minute (<c>docs/ai/README.md</c> §4) and deletes
/// finished jobs every hour (#483), indexes documents (#559) and bank label patterns (#450) and runs a queued calibration (#450) every 30 seconds,
/// and asks the decide model's guesses (#450) every 5 minutes. A loop of its own rather than Quartz jobs: the job runs journal and Quartz's store would write a row every 5 seconds for what
/// usually finds nothing to do. Each pass runs as the system actor; a pass that fails is logged and the next one runs.
/// </summary>
public sealed partial class ModelWorkLoop(IServiceScopeFactory scopes, ILogger<ModelWorkLoop> logger) : BackgroundService
{
    public static readonly TimeSpan JobsEvery = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan WorkersEvery = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan PurgeEvery = TimeSpan.FromHours(1);

    /// <summary>#559: documents' chunks and embeddings, a few documents a pass.</summary>
    public static readonly TimeSpan IndexEvery = TimeSpan.FromSeconds(30);

    /// <summary>#450: the decide model's guesses, a few requests a pass; slower, since a group held back (an adult's allow missing) is asked
    /// again each pass and each refusal is an <c>llm_call</c> row.</summary>
    public static readonly TimeSpan GuessEvery = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(JobsEvery);
        // Counted in ticks, not read off a clock: a test's frozen IClock would stop the slower passes after their first run.
        for (var tick = 0L; await timer.WaitForNextTickAsync(stoppingToken); tick++)
        {
            if (Due(tick, WorkersEvery))
            {
                await PassAsync<IWorkerMonitor>((m, ct) => m.RunOnceAsync(ct), stoppingToken);
                await PassAsync<IBudgetSweep>((s, ct) => s.RunOnceAsync(ct), stoppingToken); // #488: holds no answer settled
            }

            await PassAsync<IJobDispatcher>((d, ct) => d.RunOnceAsync(ct), stoppingToken);

            if (Due(tick, IndexEvery))
            {
                await PassAsync<Nid.Documents.Application.DocumentIndexer>((i, ct) => i.RunOnceAsync(ct), stoppingToken);
                await PassAsync<Nid.Banking.Application.PatternIndexer>((i, ct) => i.RunOnceAsync(ct), stoppingToken); // #450
                await PassAsync<Nid.Banking.Application.DecideCalibrator>((c, ct) => c.RunOnceAsync(ct), stoppingToken); // #450: one a pass
            }

            if (Due(tick, GuessEvery))
                await PassAsync<Nid.Banking.Application.CategoryGuesser>((g, ct) => g.RunOnceAsync(ct), stoppingToken);

            if (Due(tick, PurgeEvery))
            {
                await PassAsync<IJobPurge>((p, ct) => p.RunOnceAsync(ct), stoppingToken);
                await PassAsync<IConversationPurge>((p, ct) => p.RunOnceAsync(ct), stoppingToken); // #531: past their retention
            }
        }
    }

    /// <summary>The first tick runs every pass, then each pass every <paramref name="every"/>, in ticks of <see cref="JobsEvery"/>.</summary>
    public static bool Due(long tick, TimeSpan every) => tick % (every.Ticks / JobsEvery.Ticks) == 0;

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
