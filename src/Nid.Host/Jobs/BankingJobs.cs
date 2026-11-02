using Nid.Banking.Application.Sync;
using Nid.Platform.Contracts;
using Quartz;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Host.Jobs;

public static class BankingJobs
{
    public static readonly JobKey NightlyKey = new("bank-sync-nightly");
}

/// <summary>
/// Sends <see cref="SyncAllCommand"/> as the system actor, like <c>prices-daily</c> (bank sync D6). A refusal (bank sync off or not
/// configured) is logged as information, not as an error. Never throws.
/// </summary>
[DisallowConcurrentExecution]
public sealed partial class BankSyncNightlyJob(IServiceScopeFactory scopes, ILogger<BankSyncNightlyJob> logger) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            var result = await scope.ServiceProvider.GetRequiredService<IInvoker>().InvokeAsync(new SyncAllCommand(), cancellationToken);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "bank-sync-nightly was interrupted")]
    private partial void LogInterrupted();

    [LoggerMessage(Level = LogLevel.Information, Message = "bank-sync-nightly did not run: {Code}")]
    private partial void LogRefused(string code);

    [LoggerMessage(Level = LogLevel.Error, Message = "bank-sync-nightly crashed")]
    private partial void LogCrashed(Exception exception);
}
