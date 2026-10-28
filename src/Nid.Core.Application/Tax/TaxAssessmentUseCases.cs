using Nid.Core.Contracts;
using Nid.Core.Domain.Tax;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Core.Application.Tax;

/// <summary>
/// Tax assessments spec §6: the reader's notices; <c>Household</c> keeps those both adults declare (or the only active adult's), <c>Me</c> all
/// the reader's.
/// </summary>
public sealed record ListTaxAssessmentsQuery(VisibilityScope Scope, int? Year) : IQuery<TaxAssessmentList>;

public sealed record GetTaxAssessmentQuery(Guid Id) : IQuery<TaxAssessmentView>;

/// <summary>Tax assessments spec §4: the actor must be one of the declarants.</summary>
public sealed record AddTaxAssessmentCommand(TaxAssessment Assessment) : ICommand<TaxAssessmentView>;

/// <summary>Tax assessments spec §4: replaces the notice; <c>Version</c> is the one read with it.</summary>
public sealed record CorrectTaxAssessmentCommand(Guid Id, TaxAssessment Assessment, uint Version) : ICommand<TaxAssessmentView>;

public sealed record DeleteTaxAssessmentCommand(Guid Id) : ICommand<Guid>;

[RequestHandler<ListTaxAssessmentsQuery, TaxAssessmentList>]
public sealed class ListTaxAssessmentsHandler(ITaxAssessmentStore store, IPersonRegistry persons, IActorAccessor actor)
    : IRequestHandler<ListTaxAssessmentsQuery, TaxAssessmentList>
{
    public async ValueTask<Result<TaxAssessmentList>> HandleAsync(ListTaxAssessmentsQuery query, CancellationToken ct = default)
    {
        var stored = await store.ListForAsync(actor.Current!.PersonId!.Value, query.Year, ct);
        var people = (await persons.ListAsync(ct)).ToDictionary(p => p.Id.Value);
        if (query.Scope == VisibilityScope.Household)
            stored = stored.Where(s => TaxAssessmentAccess.InHousehold(s.Assessment, people.Values.ToList())).ToList();
        string FirstName(StoredTaxAssessment s)
            => people.GetValueOrDefault(s.Assessment.Declarants.OrderBy(d => d.Rank).First().Person.Value)?.DisplayName ?? "";
        return Result.Success(new TaxAssessmentList(stored.OrderByDescending(s => s.Assessment.Year)
            .ThenBy(FirstName, StringComparer.CurrentCultureIgnoreCase).Select(TaxAssessmentView.Of).ToList()));
    }
}

[RequestHandler<GetTaxAssessmentQuery, TaxAssessmentView>]
public sealed class GetTaxAssessmentHandler(ITaxAssessmentStore store, IActorAccessor actor) : IRequestHandler<GetTaxAssessmentQuery, TaxAssessmentView>
{
    public async ValueTask<Result<TaxAssessmentView>> HandleAsync(GetTaxAssessmentQuery query, CancellationToken ct = default)
        => await TaxAssessmentAccess.OwnAsync(store, actor, query.Id, ct) is { } own
            ? Result.Success(TaxAssessmentView.Of(own))
            : Result.Failure<TaxAssessmentView>(TaxAssessmentAccess.NotFound(query.Id));
}

[RequestHandler<AddTaxAssessmentCommand, TaxAssessmentView>]
public sealed class AddTaxAssessmentHandler(ITaxAssessmentStore store, IPersonRegistry persons, IHouseholdCalendar calendar, IActorAccessor actor)
    : IRequestHandler<AddTaxAssessmentCommand, TaxAssessmentView>
{
    public async ValueTask<Result<TaxAssessmentView>> HandleAsync(AddTaxAssessmentCommand command, CancellationToken ct = default)
    {
        var a = command.Assessment;
        if (await TaxAssessmentAccess.CheckAsync(store, persons, calendar, actor, a, null, ct) is { } failure)
            return Result.Failure<TaxAssessmentView>(failure);
        Guid id;
        try
        {
            id = await store.AddAsync(a, ct);
        }
        catch (TaxAssessmentTakenException)
        {
            return Result.Failure<TaxAssessmentView>(await TaxAssessmentAccess.TakenAsync(store, a, null, ct));
        }
        return Result.Success(TaxAssessmentView.Of((await store.FindAsync(id, ct))!));
    }
}

[RequestHandler<CorrectTaxAssessmentCommand, TaxAssessmentView>]
public sealed class CorrectTaxAssessmentHandler(ITaxAssessmentStore store, IPersonRegistry persons, IHouseholdCalendar calendar, IActorAccessor actor)
    : IRequestHandler<CorrectTaxAssessmentCommand, TaxAssessmentView>
{
    public async ValueTask<Result<TaxAssessmentView>> HandleAsync(CorrectTaxAssessmentCommand command, CancellationToken ct = default)
    {
        if (await TaxAssessmentAccess.OwnAsync(store, actor, command.Id, ct) is not { } own)
            return Result.Failure<TaxAssessmentView>(TaxAssessmentAccess.NotFound(command.Id));
        if (own.Version != command.Version)
            return Result.Failure<TaxAssessmentView>(TaxAssessmentAccess.Changed);
        // A correction without a source keeps the stored one.
        var a = command.Assessment with { DocumentId = command.Assessment.DocumentId ?? own.Assessment.DocumentId };
        if (await TaxAssessmentAccess.CheckAsync(store, persons, calendar, actor, a, command.Id, ct) is { } failure)
            return Result.Failure<TaxAssessmentView>(failure);
        try
        {
            await store.UpdateAsync(command.Id, a, command.Version, ct);
        }
        catch (TaxAssessmentChangedException)
        {
            return Result.Failure<TaxAssessmentView>(TaxAssessmentAccess.Changed);
        }
        catch (TaxAssessmentTakenException)
        {
            return Result.Failure<TaxAssessmentView>(await TaxAssessmentAccess.TakenAsync(store, a, command.Id, ct));
        }
        return Result.Success(TaxAssessmentView.Of((await store.FindAsync(command.Id, ct))!));
    }
}

[RequestHandler<DeleteTaxAssessmentCommand, Guid>]
public sealed class DeleteTaxAssessmentHandler(ITaxAssessmentStore store, IActorAccessor actor) : IRequestHandler<DeleteTaxAssessmentCommand, Guid>
{
    public async ValueTask<Result<Guid>> HandleAsync(DeleteTaxAssessmentCommand command, CancellationToken ct = default)
    {
        if (await TaxAssessmentAccess.OwnAsync(store, actor, command.Id, ct) is null)
            return Result.Failure<Guid>(TaxAssessmentAccess.NotFound(command.Id));
        await store.DeleteAsync(command.Id, ct);
        return Result.Success(command.Id);
    }
}

/// <summary>Tax assessments spec §4: who may touch a notice and what it must satisfy; the reads and writes share it.</summary>
internal static class TaxAssessmentAccess
{
    public static Failure Changed { get; } = new(TaxAssessmentCodes.Conflict, "The tax assessment changed since it was read.", null);

    /// <summary>
    /// Whether the notice counts in the household scope: both adults declare it (two declarants are both adults, validated), or the household
    /// has a single active adult (pending invitations excluded) and they are its only declarant.
    /// </summary>
    public static bool InHousehold(TaxAssessment a, IReadOnlyList<PersonInfo> people)
    {
        if (a.Declarants.Count == 2)
            return true;
        var active = people.Where(p => p is { Kind: PersonKind.Adult, IsPending: false }).ToList();
        return active.Count == 1 && a.Declarants.All(d => d.Person.Value == active[0].Id.Value);
    }

    public static Failure NotFound(Guid id) => new NotFoundFailure("tax assessment", id.ToString());

    /// <summary>The notice, only when the actor is one of its declarants.</summary>
    public static async Task<StoredTaxAssessment?> OwnAsync(ITaxAssessmentStore store, IActorAccessor actor, Guid id, CancellationToken ct)
    {
        var me = actor.Current!.PersonId!.Value;
        return await store.FindAsync(id, ct) is { } s && s.Assessment.Declarants.Any(d => d.Person == me) ? s : null;
    }

    /// <summary>The first refusal of an added or corrected notice, null when it may be stored; <paramref name="except"/> is the notice being corrected.</summary>
    public static async Task<Failure?> CheckAsync(
        ITaxAssessmentStore store, IPersonRegistry persons, IHouseholdCalendar calendar, IActorAccessor actor, TaxAssessment a, Guid? except, CancellationToken ct)
    {
        var adults = (await persons.AdultIdsAsync(ct)).Select(p => p.Value).ToHashSet();
        var errors = TaxAssessmentRules.Check(a, (await calendar.TodayAsync(ct)).Year, adults);
        if (errors.Count > 0)
            return FieldErrors.Many(TaxAssessmentCodes.InvalidAssessment, errors.Count == 1 ? errors[0].Message : "Some fields are not valid.",
                errors.Select(e => new FieldError(e.Path, TaxAssessmentCodes.InvalidAssessment, e.Message)).ToList());
        if (a.Declarants.All(d => d.Person != actor.Current!.PersonId!.Value))
            return FieldErrors.At(new Failure(TaxAssessmentCodes.DeclarantRequired, "You must be one of the declarants.", null), "declarants");
        return await store.PersonTakenAsync(a.Year, a.Declarants.Select(d => d.Person).ToList(), except, ct)
            ? await TakenAsync(store, a, except, ct)
            : null;
    }

    /// <summary>The declarant already in another notice of the year, located; the first one when a concurrent write took them meanwhile.</summary>
    public static async Task<Failure> TakenAsync(ITaxAssessmentStore store, TaxAssessment a, Guid? except, CancellationToken ct)
    {
        var index = 0;
        for (var i = 0; i < a.Declarants.Count; i++)
            if (await store.PersonTakenAsync(a.Year, [a.Declarants[i].Person], except, ct))
            {
                index = i;
                break;
            }
        return FieldErrors.At(new Failure(TaxAssessmentCodes.DeclarantTaken, "This person already declares a notice for that year.", null),
            $"declarants[{index}].personId");
    }
}
