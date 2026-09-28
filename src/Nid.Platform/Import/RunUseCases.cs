using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Platform.Import;

public sealed record ValidateImportCommand(Guid SessionId, bool Replace = false) : ICommand<ImportSessionView>, IDirectWrite;

public sealed record CommitImportCommand(Guid SessionId, bool Replace = false) : ICommand<ImportSessionView>, IDirectWrite;

/// <summary>60 s by default (§11.5): past it, everything is abandoned and the import is too large to run at once.</summary>
internal static class Bounded
{
    public static async Task<Result<T>> RunAsync<T>(TimeSpan limit, CancellationToken ct, Func<CancellationToken, Task<Result<T>>> work)
        where T : notnull
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit);
        try
        {
            return await work(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Result.Failure<T>(new Failure(ImportCodes.TooLarge,
                "The import did not finish in time and was abandoned: split it into smaller imports.", null));
        }
    }
}

[RequestHandler<ValidateImportCommand, ImportSessionView>]
public sealed class ValidateImportHandler(
    ImportRunner runner, IImportStaging staging, IActorAccessor accessor, IClock clock, ImportOptions options)
    : IRequestHandler<ValidateImportCommand, ImportSessionView>
{
    public async ValueTask<Result<ImportSessionView>> HandleAsync(ValidateImportCommand command, CancellationToken ct = default)
    {
        if (!ImportActor.Require(accessor).TryGetValue(out var person))
            return Result.Failure<ImportSessionView>(new UnauthorizedFailure("An import is run by a person.", null));

        var session = await ImportActor.FindAsync(staging, clock, command.SessionId, person, ct);
        if (session is null)
            return Result.Failure<ImportSessionView>(ImportActor.NotFound(command.SessionId));
        if (!ImportStates.Active.Contains(session.State))
            return Result.Failure<ImportSessionView>(new Failure(ImportCodes.State,
                $"This import is {ImportStates.Name(session.State)}: there is nothing left to validate.", null));

        var validated = await Bounded.RunAsync(options.OperationTimeout, ct,
            async token => Result.Success(await runner.ValidateAsync(session, command.Replace, token)));
        if (!validated.TryGetValue(out var report))
            return Result.Failure<ImportSessionView>(validated.TryGetFailure(out var failure) ? failure! : new Failure("ERROR", "Unknown failure.", null));

        var now = clock.GetCurrentInstant();
        var state = report.State == ImportRunner.Validated ? ImportState.Validated : ImportState.Invalid;
        var expiresAt = now.Plus(NodaTime.Duration.FromTimeSpan(options.SessionLifetime));
        // Stored only over the session this report was computed from. A batch that arrived meanwhile moved the
        // session (its move and its insert are one transaction, so this update waits for both, then sees the
        // move): the report misses that batch and is not stored.
        if (!await staging.TransitionAsync(session.Id, ImportStates.Active.ToList(), state, now, expiresAt,
                ImportReportJson.Serialize(report), ct, expectedUpdatedAt: session.UpdatedAt))
            return Result.Failure<ImportSessionView>(new Failure(ImportCodes.State,
                "The import changed while it was being validated; validate again.", null));

        return Result.Success(ImportSessionView.From(session with { State = state, ExpiresAt = expiresAt, ReportJson = ImportReportJson.Serialize(report) }));
    }
}

[RequestHandler<CommitImportCommand, ImportSessionView>]
public sealed class CommitImportHandler(
    ImportRunner runner, IImportStaging staging, IActorAccessor accessor, IClock clock, ImportOptions options, IImportAudit audit)
    : IRequestHandler<CommitImportCommand, ImportSessionView>
{
    public async ValueTask<Result<ImportSessionView>> HandleAsync(CommitImportCommand command, CancellationToken ct = default)
    {
        if (!ImportActor.Require(accessor).TryGetValue(out var person))
            return Result.Failure<ImportSessionView>(new UnauthorizedFailure("An import is run by a person.", null));

        var session = await ImportActor.FindAsync(staging, clock, command.SessionId, person, ct);
        if (session is null)
            return Result.Failure<ImportSessionView>(ImportActor.NotFound(command.SessionId));
        if (!ImportStates.Active.Contains(session.State))
            return Result.Failure<ImportSessionView>(new Failure(ImportCodes.State,
                $"This import is {ImportStates.Name(session.State)}: there is nothing left to commit.", null));

        // 1F3b spec §4.3: what the commit writes is written "through import", by the importer.
        var browser = accessor.Current!;
        accessor.Current = browser with { Via = ActorOrigin.Import };
        Result<ImportReport> committed;
        try
        {
            committed = await Bounded.RunAsync(options.OperationTimeout, ct, token => runner.CommitAsync(session, command.Replace, token));
        }
        finally
        {
            accessor.Current = browser;
        }

        if (!committed.TryGetValue(out var report))
            return Result.Failure<ImportSessionView>(committed.TryGetFailure(out var failure) ? failure! : new Failure("ERROR", "Unknown failure.", null));

        return Result.Success(ImportSessionView.From(
            session with { State = ImportState.Committed, ReportJson = ImportReportJson.Serialize(report) },
            await audit.FindAsync(session.Id, ct)));
    }
}
