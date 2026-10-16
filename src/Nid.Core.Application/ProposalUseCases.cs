using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Core.Application;

/// <summary>Spec 4C1 D3: one handler per kind, found by its kind.</summary>
public sealed class ProposalHandlers(IEnumerable<IProposalHandler> handlers)
{
    private readonly Dictionary<string, IProposalHandler> byKind = handlers.ToDictionary(h => h.Kind, StringComparer.Ordinal);

    public IProposalHandler? Find(string? kind) => kind is null ? null : byKind.GetValueOrDefault(kind);
}

public sealed record ProposalView(
    Guid Id, string Kind, Guid TargetItemId, string Status, JsonElement Payload, JsonElement? EditedPayload, int PayloadVersion,
    IReadOnlyList<ProposalWarning> Warnings, string? SubjectKey, string? SourceLabel, string CreatedVia, DateTimeOffset CreatedAt,
    Guid CreatedByPersonId, Guid? SupersededById, DateTimeOffset? DecidedAt, Guid? DecidedByPersonId, string? DecisionNote,
    IReadOnlyList<Guid> AppliedRefs, uint Version, string? TargetName)
{
    public static ProposalView From(StoredProposal p, string? targetName, IReadOnlyList<ProposalWarning>? extra = null) => new(
        p.Id, p.Kind, p.Target.Value, p.Status.ToString().ToLowerInvariant(), p.Payload, p.EditedPayload, p.PayloadVersion,
        extra is null ? p.Warnings : [.. p.Warnings, .. extra], p.SubjectKey, p.SourceLabel, p.CreatedVia,
        p.CreatedAt.ToDateTimeOffset(), p.CreatedBy.Value, p.SupersededById, p.DecidedAt?.ToDateTimeOffset(), p.DecidedBy?.Value,
        p.DecisionNote, p.AppliedRefs, p.Version, targetName);
}

/// <summary><see cref="Proposal"/> is null when the document matches what Nid expects (<see cref="Conforme"/>, spec 4D1 D3): nothing was stored.</summary>
public sealed record SubmittedProposal(ProposalView? Proposal, bool Created, JsonElement? Conforme = null) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Proposal is null ? [] : [new ItemId(Proposal.TargetItemId)];
}

public sealed record ProposalList(IReadOnlyList<ProposalView> Items) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Items.Select(p => new ItemId(p.TargetItemId)).Distinct();
}

public sealed record ProposalDetail(ProposalView Proposal, JsonElement? Preview, bool CanDecide) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => [new ItemId(Proposal.TargetItemId)];
}

public sealed record SubmitProposalCommand(string Kind, ItemId ItemId, JsonElement Payload, string? SourceLabel)
    : ICommand<SubmittedProposal>, IItemScoped, IRequiresScope
{
    public AccessKind Access => AccessKind.Write;
    public string RequiredScope => ScopeNames.Write;
}

public sealed record ListProposalsQuery(string? Status) : IQuery<ProposalList>;

public sealed record GetProposalQuery(Guid Id) : IQuery<ProposalDetail>;

public sealed record ApproveProposalCommand(Guid Id, uint Version, JsonElement? EditedPayload) : ICommand<ProposalView>, IBrowserOnly;

public sealed record RejectProposalCommand(Guid Id, uint Version, string? Note) : ICommand<ProposalView>, IBrowserOnly;

/// <summary>
/// Spec 4D1 §5: a document written directly — the kind's validation then its application in one unit of work, as the
/// token's actor; no proposal row (the write log records the module's rows).
/// </summary>
public sealed record ApplyDirectCommand(string Kind, ItemId ItemId, JsonElement Payload) : ICommand<DirectWrite>, IItemScoped, IDirectWrite
{
    public AccessKind Access => AccessKind.Write;
}

/// <summary>`applied` with what was written and what was left out as already recorded, or `conforme` with nothing written.</summary>
public sealed record DirectWrite(string Outcome, IReadOnlyList<Guid> AppliedRefs, IReadOnlyList<ProposalWarning> Skipped, JsonElement? Conforme);

internal static class ProposalAccess
{
    /// <summary>Spec 4C2 D2: the targets' names, in one registry read whatever the list's length.</summary>
    public static async Task<Func<ItemId, string?>> NamesAsync(IItemRegistry items, IEnumerable<ItemId> targets, CancellationToken ct)
    {
        var found = await items.FindManyAsync(targets.Distinct().ToList(), ct);
        return id => found.TryGetValue(id, out var item) ? item.DisplayName : null;
    }

    /// <summary>The proposal, if the actor may <paramref name="access"/> its target: unreadable answers not found (authorization.md §6).</summary>
    public static async Task<Result<(StoredProposal Proposal, PersonId Me)>> LoadAsync(
        IProposalStore store, IAccessPolicy policy, IActorAccessor accessor, Guid id, AccessKind access, CancellationToken ct)
    {
        if (accessor.Current?.PersonId is not { } me)
            return Result.Failure<(StoredProposal, PersonId)>(new UnauthorizedFailure("A person decides proposals.", null));
        var proposal = await store.FindAsync(id, ct);
        if (proposal is null || (await policy.CheckAsync(me, proposal.Target, AccessKind.Read, ct)).TryGetFailure(out _))
            return Result.Failure<(StoredProposal, PersonId)>(new NotFoundFailure("proposal", id.ToString()));
        if (access == AccessKind.Write && (await policy.CheckAsync(me, proposal.Target, AccessKind.Write, ct)).TryGetFailure(out var denied))
            return Result.Failure<(StoredProposal, PersonId)>(denied!);
        return Result.Success((proposal, me));
    }

    /// <summary>A handler reads an object; anything else (absent, null, array, scalar) is refused before it runs.</summary>
    public static Result Payload(JsonElement payload, string path)
        => payload.ValueKind == JsonValueKind.Object
            ? Result.Success()
            : Result.Failure(FieldErrors.At(new Failure(FieldErrors.ShapeCode, "A JSON object is required.", null), path));

    public static Result Text(string? value, int max, string path)
        => value is not null && (value.Trim().Length == 0 || value.Trim().Length > max)
            ? Result.Failure(FieldErrors.At(new Failure("request.invalid", $"1 to {max} characters.", null), path))
            : Result.Success();
}

[RequestHandler<SubmitProposalCommand, SubmittedProposal>]
public sealed class SubmitProposalHandler(ProposalHandlers handlers, IProposalStore store, IActorAccessor accessor, IItemRegistry items)
    : IRequestHandler<SubmitProposalCommand, SubmittedProposal>
{
    public async ValueTask<Result<SubmittedProposal>> HandleAsync(SubmitProposalCommand command, CancellationToken ct = default)
    {
        var actor = accessor.Current!;
        if (actor.PersonId is not { } me)
            return Result.Failure<SubmittedProposal>(new UnauthorizedFailure("A person submits proposals.", null));
        if (ProposalAccess.Text(command.SourceLabel, 200, "sourceLabel").TryGetFailure(out var invalid))
            return Result.Failure<SubmittedProposal>(invalid!);
        if (handlers.Find(command.Kind) is not { } handler)
            return Result.Failure<SubmittedProposal>(new Failure(ProposalCodes.KindUnknown, $"'{command.Kind}' is not a proposal kind.", null));
        if (ProposalAccess.Payload(command.Payload, "payload").TryGetFailure(out var notObject))
            return Result.Failure<SubmittedProposal>(notObject!);

        var checkedPayload = await handler.ValidateAsync(command.ItemId, command.Payload, ct);
        if (!checkedPayload.TryGetValue(out var check))
            return Result.Failure<SubmittedProposal>(checkedPayload.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());

        if (check!.Conforme is { } conforme)
            return Result.Success(new SubmittedProposal(null, false, conforme));

        var outcome = await store.SubmitAsync(new NewProposal(handler.Module, handler.Kind, command.ItemId, me, check.SubjectKey,
            check.Payload, handler.PayloadVersion, check.Warnings, actor.Via.ToWire(), actor.TokenId, command.SourceLabel?.Trim()), ct);
        var names = await ProposalAccess.NamesAsync(items, [outcome.Proposal.Target], ct);
        return Result.Success(new SubmittedProposal(ProposalView.From(outcome.Proposal, names(outcome.Proposal.Target)), outcome.Created));
    }
}

[RequestHandler<ListProposalsQuery, ProposalList>]
public sealed class ListProposalsHandler(IProposalStore store, IVisibleItems visible, IActorAccessor accessor, IItemRegistry items)
    : IRequestHandler<ListProposalsQuery, ProposalList>
{
    public async ValueTask<Result<ProposalList>> HandleAsync(ListProposalsQuery query, CancellationToken ct = default)
    {
        if (accessor.Current?.PersonId is not { } me)
            return Result.Failure<ProposalList>(new UnauthorizedFailure("A person reads proposals.", null));
        if (!Enum.TryParse<ProposalStatus>(query.Status ?? "pending", ignoreCase: true, out var status) || !Enum.IsDefined(status))
            return Result.Failure<ProposalList>(FieldErrors.At(new Failure("request.invalid", "pending, approved, rejected or superseded.", null), "status"));
        var targets = await visible.ForAsync(me, VisibilityScope.Me, includeArchived: true, ct);
        var stored = await store.ListAsync(targets, status, ct);
        var names = await ProposalAccess.NamesAsync(items, stored.Select(p => p.Target), ct);
        return Result.Success(new ProposalList(stored.Select(p => ProposalView.From(p, names(p.Target))).ToList()));
    }
}

[RequestHandler<GetProposalQuery, ProposalDetail>]
public sealed class GetProposalHandler(
    IProposalStore store, IAccessPolicy policy, IActorAccessor accessor, ProposalHandlers handlers, IItemRegistry items)
    : IRequestHandler<GetProposalQuery, ProposalDetail>
{
    public async ValueTask<Result<ProposalDetail>> HandleAsync(GetProposalQuery query, CancellationToken ct = default)
    {
        var loaded = await ProposalAccess.LoadAsync(store, policy, accessor, query.Id, AccessKind.Read, ct);
        if (!loaded.TryGetValue(out var found))
            return Result.Failure<ProposalDetail>(loaded.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());
        var (proposal, me) = found;
        var canDecide = (await policy.CheckAsync(me, proposal.Target, AccessKind.Write, ct)).IsSuccess && proposal.Status == ProposalStatus.Pending
            && accessor.Current?.TokenId is null; // deciding is browser-only
        var names = await ProposalAccess.NamesAsync(items, [proposal.Target], ct);
        var handler = handlers.Find(proposal.Kind);
        if (handler is null || handler.PayloadVersion != proposal.PayloadVersion)
            return Result.Success(new ProposalDetail(ProposalView.From(proposal, names(proposal.Target), proposal.Status == ProposalStatus.Pending
                ? [new ProposalWarning(ProposalCodes.PayloadVersionUnsupported, "error", "This proposal can only be rejected.")] : null), null, canDecide));
        var preview = proposal.Status == ProposalStatus.Pending
            ? await handler.PreviewAsync(proposal.Target, proposal.EditedPayload ?? proposal.Payload, ct) : (JsonElement?)null;
        return Result.Success(new ProposalDetail(ProposalView.From(proposal, names(proposal.Target)), preview, canDecide));
    }
}

[RequestHandler<ApproveProposalCommand, ProposalView>]
public sealed class ApproveProposalHandler(
    IProposalStore store, IAccessPolicy policy, IActorAccessor accessor, ProposalHandlers handlers, IUnitOfWork unitOfWork,
    IItemRegistry items, ILogger<ApproveProposalHandler> logger)
    : IRequestHandler<ApproveProposalCommand, ProposalView>
{
    public async ValueTask<Result<ProposalView>> HandleAsync(ApproveProposalCommand command, CancellationToken ct = default)
    {
        var loaded = await ProposalAccess.LoadAsync(store, policy, accessor, command.Id, AccessKind.Write, ct);
        if (!loaded.TryGetValue(out var found))
            return Result.Failure<ProposalView>(loaded.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());
        var (proposal, me) = found;
        if (proposal.Status != ProposalStatus.Pending)
            return Fail(ProposalCodes.Decided, "This proposal was already decided or replaced.");
        if (proposal.Version != command.Version)
            return Fail(ProposalCodes.Conflict, "This proposal changed meanwhile: reload it.");
        if (handlers.Find(proposal.Kind) is not { } handler || handler.PayloadVersion != proposal.PayloadVersion)
            return Fail(ProposalCodes.VersionUnsupported, "This proposal's payload is no longer read: reject it.");

        // Read before anything is written: a failing read then leaves no decision behind.
        var names = await ProposalAccess.NamesAsync(items, [proposal.Target], ct);
        var payload = command.EditedPayload ?? proposal.Payload;
        if (ProposalAccess.Payload(payload, "editedPayload").TryGetFailure(out var notObject))
            return Result.Failure<ProposalView>(notObject!);
        Failure? refused = null;
        try
        {
            await using var transaction = await unitOfWork.BeginAsync(ct);
            var check = await handler.ValidateAsync(proposal.Target, payload, ct);
            if (check.TryGetValue(out var valid))
            {
                // Applied and kept normalized, as a submission is.
                // A payload that now matches what Nid expects (« conforme ») has nothing to apply: approved, nothing written.
                var applied = valid!.Conforme is not null
                    ? Result.Success<IReadOnlyList<Guid>>([])
                    : await handler.ApplyAsync(proposal.Target, valid.Payload, ct);
                if (applied.TryGetValue(out var refs))
                {
                    var decided = await store.DecideAsync(proposal.Id, command.Version, ProposalStatus.Approved, me,
                        command.EditedPayload is null ? null : valid.Payload, null, refs!, ct);
                    if (!decided.TryGetValue(out var stored))
                        return Result.Failure<ProposalView>(decided.TryGetFailure(out var lost) ? lost! : throw new InvalidOperationException());
                    await transaction.CommitAsync(ct);
                    return Result.Success(ProposalView.From(stored!, names(stored!.Target)));
                }
                applied.TryGetFailure(out refused);
            }
            else
                check.TryGetFailure(out refused);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Rolled back (the scope was disposed on the way out): the type only, the text may carry internals; the 500 stays.
            try
            {
                await WarnAsync(proposal.Id, e.GetType().Name, "The application threw.", ct);
            }
            catch (Exception warnFailed) when (warnFailed is not OperationCanceledException)
            {
                // The original exception is the one to surface.
                logger.LogWarning(warnFailed, "Could not add apply_failed to proposal {ProposalId}", proposal.Id);
            }
            throw;
        }

        // Rolled back above (the scope was disposed without a commit): the warning is the only trace, in its own write.
        await WarnAsync(proposal.Id, refused!.Code, refused.Message, ct);
        return Result.Failure<ProposalView>(refused);
    }

    private Task WarnAsync(Guid id, string code, string message, CancellationToken ct)
        => store.AddWarningAsync(id, new ProposalWarning(ProposalCodes.ApplyFailed, "error", $"{code}: {message}"), ct);

    private static Result<ProposalView> Fail(string code, string message) => Result.Failure<ProposalView>(new Failure(code, message, null));
}

[RequestHandler<RejectProposalCommand, ProposalView>]
public sealed class RejectProposalHandler(IProposalStore store, IAccessPolicy policy, IActorAccessor accessor, IItemRegistry items)
    : IRequestHandler<RejectProposalCommand, ProposalView>
{
    public async ValueTask<Result<ProposalView>> HandleAsync(RejectProposalCommand command, CancellationToken ct = default)
    {
        var note = string.IsNullOrWhiteSpace(command.Note) ? null : command.Note.Trim();
        if (ProposalAccess.Text(note, 500, "note").TryGetFailure(out var invalid))
            return Result.Failure<ProposalView>(invalid!);
        var loaded = await ProposalAccess.LoadAsync(store, policy, accessor, command.Id, AccessKind.Write, ct);
        if (!loaded.TryGetValue(out var found))
            return Result.Failure<ProposalView>(loaded.TryGetFailure(out var failure) ? failure! : throw new InvalidOperationException());
        // Read before the decision is written: a failing read then leaves no decision behind.
        var names = await ProposalAccess.NamesAsync(items, [found.Proposal.Target], ct);
        var decided = await store.DecideAsync(found.Proposal.Id, command.Version, ProposalStatus.Rejected, found.Me, null, note, [], ct);
        if (!decided.TryGetValue(out var stored))
            return Result.Failure<ProposalView>(decided.TryGetFailure(out var lost) ? lost! : throw new InvalidOperationException());
        return Result.Success(ProposalView.From(stored, names(stored.Target)));
    }
}

[RequestHandler<ApplyDirectCommand, DirectWrite>]
public sealed class ApplyDirectHandler(ProposalHandlers handlers, IUnitOfWork unitOfWork) : IRequestHandler<ApplyDirectCommand, DirectWrite>
{
    public async ValueTask<Result<DirectWrite>> HandleAsync(ApplyDirectCommand command, CancellationToken ct = default)
    {
        if (handlers.Find(command.Kind) is not { } handler)
            return Result.Failure<DirectWrite>(new Failure(ProposalCodes.KindUnknown, $"'{command.Kind}' is not a proposal kind.", null));
        if (ProposalAccess.Payload(command.Payload, "payload").TryGetFailure(out var notObject))
            return Result.Failure<DirectWrite>(notObject!);

        // Every early return skips the commit and so rolls back whatever the application wrote: never commit after a refusal.
        await using var transaction = await unitOfWork.BeginAsync(ct);
        var check = await handler.ValidateAsync(command.ItemId, command.Payload, ct);
        if (!check.TryGetValue(out var valid))
            return Result.Failure<DirectWrite>(check.TryGetFailure(out var refused) ? refused! : throw new InvalidOperationException());
        if (valid!.Conforme is { } conforme)
            return Result.Success(new DirectWrite("conforme", [], [], conforme));
        var applied = await handler.ApplyAsync(command.ItemId, valid.Payload, ct);
        if (!applied.TryGetValue(out var refs))
            return Result.Failure<DirectWrite>(applied.TryGetFailure(out var failed) ? failed! : throw new InvalidOperationException());
        await transaction.CommitAsync(ct);
        return Result.Success(new DirectWrite("applied", refs!,
            valid.Warnings.Where(w => w.Code == ProposalCodes.AlreadyRecorded).ToList(), null));
    }
}
