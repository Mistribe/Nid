using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Platform.Import;

public sealed record StartImportCommand(string Format, int Version, IReadOnlyDictionary<string, PersonId> Adults)
    : ICommand<ImportSessionView>, IDirectWrite, IBrowserOnly;

public sealed record GetImportQuery(Guid SessionId) : IQuery<ImportSessionView>, IDirectWrite, IBrowserOnly;

public sealed record ListImportsQuery : IQuery<IReadOnlyList<ImportSessionView>>, IDirectWrite, IBrowserOnly;

public sealed record CancelImportCommand(Guid SessionId) : ICommand<ImportSessionView>, IDirectWrite, IBrowserOnly;

/// <summary>An import belongs to a person: the session, its report and what it writes are theirs.</summary>
public static class ImportActor
{
    public static Result<PersonId> Require(IActorAccessor accessor)
        => accessor.Current?.PersonId is { } person
            ? Result.Success(person)
            : Result.Failure<PersonId>(new UnauthorizedFailure("An import is run by a person.", null));

    public static Failure NotFound(Guid session) => new NotFoundFailure("import", session.ToString());

    /// <summary>
    /// The one lookup of every call on a session: the owner's session (§4, ownership) as it stands now, so an
    /// unswept session past its lifetime is expired for every handler alike (§11.5).
    /// </summary>
    public static async Task<ImportSession?> FindAsync(IImportStaging staging, IClock clock, Guid id, PersonId person, CancellationToken ct)
        => (await staging.FindOwnedAsync(id, person, ct))?.AsOf(clock.GetCurrentInstant());
}

[RequestHandler<StartImportCommand, ImportSessionView>]
public sealed class StartImportHandler(IImportStaging staging, IActorAccessor accessor, IClock clock, ImportOptions options)
    : IRequestHandler<StartImportCommand, ImportSessionView>
{
    public async ValueTask<Result<ImportSessionView>> HandleAsync(StartImportCommand command, CancellationToken ct = default)
    {
        if (!ImportActor.Require(accessor).TryGetValue(out var person))
            return Result.Failure<ImportSessionView>(new UnauthorizedFailure("An import is run by a person.", null));

        if (command.Format != NidDataFormat.Name || command.Version != NidDataFormat.Version)
            return Result.Failure<ImportSessionView>(new Failure(ImportCodes.UnsupportedFormat,
                $"'{command.Format}' version {command.Version} is not supported: send nid.data version 1.", null));

        var now = clock.GetCurrentInstant();
        await staging.ExpireAsync(now, ct); // on demand, no scheduler (§11.5)

        var session = await staging.CreateAsync(person, command.Adults, now, now.Plus(NodaTime.Duration.FromTimeSpan(options.SessionLifetime)), ct);
        return session is null
            ? Result.Failure<ImportSessionView>(new Failure(ImportCodes.SessionActive,
                "An import is already in progress: finish or cancel it first.", null))
            : Result.Success(ImportSessionView.From(session));
    }
}

[RequestHandler<GetImportQuery, ImportSessionView>]
public sealed class GetImportHandler(IImportStaging staging, IActorAccessor accessor, IClock clock, IImportAudit audit)
    : IRequestHandler<GetImportQuery, ImportSessionView>
{
    public async ValueTask<Result<ImportSessionView>> HandleAsync(GetImportQuery query, CancellationToken ct = default)
    {
        if (!ImportActor.Require(accessor).TryGetValue(out var person))
            return Result.Failure<ImportSessionView>(new UnauthorizedFailure("An import is run by a person.", null));

        var session = await ImportActor.FindAsync(staging, clock, query.SessionId, person, ct);
        if (session is null)
            return Result.Failure<ImportSessionView>(ImportActor.NotFound(query.SessionId));

        var entry = session.State == ImportState.Committed ? await audit.FindAsync(session.Id, ct) : null;
        return Result.Success(ImportSessionView.From(session, entry));
    }
}

[RequestHandler<ListImportsQuery, IReadOnlyList<ImportSessionView>>]
public sealed class ListImportsHandler(IImportStaging staging, IActorAccessor accessor, IClock clock, ImportOptions options)
    : IRequestHandler<ListImportsQuery, IReadOnlyList<ImportSessionView>>
{
    public async ValueTask<Result<IReadOnlyList<ImportSessionView>>> HandleAsync(ListImportsQuery query, CancellationToken ct = default)
    {
        if (!ImportActor.Require(accessor).TryGetValue(out var person))
            return Result.Failure<IReadOnlyList<ImportSessionView>>(new UnauthorizedFailure("An import is run by a person.", null));

        await staging.ExpireAsync(clock.GetCurrentInstant(), ct);
        var sessions = await staging.ListOwnedAsync(person, options.ListLimit, ct);
        return Result.Success<IReadOnlyList<ImportSessionView>>(sessions.Select(s => ImportSessionView.From(s)).ToList());
    }
}

[RequestHandler<CancelImportCommand, ImportSessionView>]
public sealed class CancelImportHandler(IImportStaging staging, IActorAccessor accessor, IClock clock)
    : IRequestHandler<CancelImportCommand, ImportSessionView>
{
    public async ValueTask<Result<ImportSessionView>> HandleAsync(CancelImportCommand command, CancellationToken ct = default)
    {
        if (!ImportActor.Require(accessor).TryGetValue(out var person))
            return Result.Failure<ImportSessionView>(new UnauthorizedFailure("An import is run by a person.", null));

        var session = await ImportActor.FindAsync(staging, clock, command.SessionId, person, ct);
        if (session is null)
            return Result.Failure<ImportSessionView>(ImportActor.NotFound(command.SessionId));
        if (!ImportStates.Active.Contains(session.State))
            return Result.Failure<ImportSessionView>(new Failure(ImportCodes.State,
                $"This import is {ImportStates.Name(session.State)}: there is nothing left to cancel.", null));

        if (!await staging.TransitionAsync(session.Id, ImportStates.Active.ToList(), ImportState.Cancelled,
                clock.GetCurrentInstant(), session.ExpiresAt, session.ReportJson, ct))
            return Result.Failure<ImportSessionView>(new Failure(ImportCodes.State,
                $"This import is {ImportStates.Name(session.State)}: there is nothing left to cancel.", null));

        await staging.ClearBatchesAsync(session.Id, ct);
        return Result.Success(ImportSessionView.From(session with { State = ImportState.Cancelled }));
    }
}
