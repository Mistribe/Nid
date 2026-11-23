using Nid.Documents.Application;
using Nid.Platform.Contracts;

namespace Nid.Host.Jobs;

/// <summary>#167: once per startup, in the background, asks for the text of the documents stored before it
/// (<see cref="DocumentTextBackfill"/>), as the system actor. Never fails or delays startup; logs the exception type only.</summary>
public sealed partial class DocumentTextBackfillStartup(IServiceScopeFactory scopes, ILogger<DocumentTextBackfillStartup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<IActorAccessor>().Current = Actor.System;
            LogDone(await scope.ServiceProvider.GetRequiredService<DocumentTextBackfill>().RunAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            LogCrashed(e.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Document text backfill queued {Documents} documents")]
    private partial void LogDone(int documents);

    [LoggerMessage(Level = LogLevel.Error, Message = "Document text backfill crashed: {ExceptionType}")]
    private partial void LogCrashed(string exceptionType);
}
