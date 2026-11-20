using Nid.Banking.Application.Reconciliation;
using Nid.Platform.Contracts;

namespace Nid.Host.Jobs;

/// <summary>
/// Once per startup, categorises the lines linked before categories existed (<see cref="CategoryBackfill"/>), then keeps savings accounts'
/// lines as their envelopes' flows (<see cref="SavingsFlowBackfill"/>), as the system actor. Idempotent,
/// not a Quartz job (no entry on the admin Jobs screen). Never fails startup; logs the exception type only.
/// </summary>
public sealed partial class CategoryBackfillStartup(IServiceScopeFactory scopes, ILogger<CategoryBackfillStartup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            LogDone(await scope.ServiceProvider.GetRequiredService<CategoryBackfill>().RunAsync(stoppingToken));
            // #16: after it, so a line's category is known before its flow is written.
            var flows = await scope.ServiceProvider.GetRequiredService<SavingsFlowBackfill>().RunAsync(stoppingToken);
            LogSavingsFlows(flows.Recorded, flows.Superseded, flows.Voided, flows.Skipped);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            LogCrashed(e.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Category backfill categorised {Lines} lines")]
    private partial void LogDone(int lines);

    [LoggerMessage(Level = LogLevel.Information, Message = "Savings flow backfill recorded {Recorded}, superseded {Superseded}, voided {Voided} flows, skipped {Skipped} accounts")]
    private partial void LogSavingsFlows(int recorded, int superseded, int voided, int skipped);

    [LoggerMessage(Level = LogLevel.Error, Message = "Category backfill crashed: {ExceptionType}")]
    private partial void LogCrashed(string exceptionType);
}
