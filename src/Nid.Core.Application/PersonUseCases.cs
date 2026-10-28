using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Core.Application;

/// <summary><paramref name="IsAdmin"/>: whether the identity provider put this adult in the admin group.</summary>
public sealed record ProvisionAdultCommand(string OidcSubject, string DisplayName, string? Email, bool IsAdmin = false) : ICommand<PersonInfo>;

public sealed record CreateDependentCommand(string DisplayName) : ICommand<PersonInfo>;

/// <summary>A null birth date clears it. Any adult sets any person's, a dependent's included: the household plans together.</summary>
public sealed record SetRetirementCommand(PersonId Person, LocalDate? BirthDate, int RetirementAge) : ICommand<PersonInfo>;

public sealed record ListPersonsQuery : IQuery<IReadOnlyList<PersonInfo>>;

[RequestHandler<ProvisionAdultCommand, PersonInfo>]
public sealed class ProvisionAdultHandler(IPersonRegistry persons) : IRequestHandler<ProvisionAdultCommand, PersonInfo>
{
    public async ValueTask<Result<PersonInfo>> HandleAsync(ProvisionAdultCommand command, CancellationToken ct = default)
        => await persons.ProvisionAdultAsync(command.OidcSubject, command.DisplayName, command.Email, command.IsAdmin, ct);
}

[RequestHandler<CreateDependentCommand, PersonInfo>]
public sealed class CreateDependentHandler(IPersonRegistry persons) : IRequestHandler<CreateDependentCommand, PersonInfo>
{
    public async ValueTask<Result<PersonInfo>> HandleAsync(CreateDependentCommand command, CancellationToken ct = default)
        => await persons.CreateDependentAsync(command.DisplayName, ct);
}

[RequestHandler<SetRetirementCommand, PersonInfo>]
public sealed class SetRetirementHandler(IPersonRegistry persons, IHouseholdCalendar calendar) : IRequestHandler<SetRetirementCommand, PersonInfo>
{
    public const int MinAge = 50;
    public const int MaxAge = 75;
    private static readonly LocalDate Earliest = new(1900, 1, 1);

    public async ValueTask<Result<PersonInfo>> HandleAsync(SetRetirementCommand command, CancellationToken ct = default)
    {
        if (command.RetirementAge is < MinAge or > MaxAge)
            return Result.Failure<PersonInfo>(FieldErrors.At(
                new Failure(PersonCodes.InvalidRetirementAge, $"The retirement age is between {MinAge} and {MaxAge}.", null), "retirementAge"));
        if (command.BirthDate is { } birth && (birth < Earliest || birth > await calendar.TodayAsync(ct)))
            return Result.Failure<PersonInfo>(FieldErrors.At(
                new Failure(PersonCodes.InvalidBirthDate, "The birth date is between 1900-01-01 and today.", null), "birthDate"));

        return await persons.SetRetirementAsync(command.Person, command.BirthDate, command.RetirementAge, ct);
    }
}

/// <summary>Whether the actor owns an active private item or a solo tax notice: without one, « Moi » shows exactly « Foyer » and the selector is hidden.</summary>
public sealed record GetMyPrivateItemsQuery : IQuery<PrivateItemsView>;

public sealed record PrivateItemsView(bool Owns);

[RequestHandler<GetMyPrivateItemsQuery, PrivateItemsView>]
public sealed class GetMyPrivateItemsHandler(IVisibleItems visible, IActorAccessor accessor, ITaxAssessmentStore assessments) : IRequestHandler<GetMyPrivateItemsQuery, PrivateItemsView>
{
    public async ValueTask<Result<PrivateItemsView>> HandleAsync(GetMyPrivateItemsQuery query, CancellationToken ct = default)
    {
        if (accessor.Current?.PersonId is not { } me)
            return Result.Success(new PrivateItemsView(false));
        // Me is Household plus the actor's private items, so a larger Me set means at least one private item.
        var mine = await visible.ForAsync(me, VisibilityScope.Me, ct);
        var household = await visible.ForAsync(me, VisibilityScope.Household, ct);
        if (mine.Count > household.Count)
            return Result.Success(new PrivateItemsView(true));
        // A solo tax notice is private too: without this the selector would hide it from a household that has no other private item.
        var notices = await assessments.ListForAsync(me, null, ct);
        return Result.Success(new PrivateItemsView(notices.Any(n => n.Assessment.Declarants.Count == 1)));
    }
}

[RequestHandler<ListPersonsQuery, IReadOnlyList<PersonInfo>>]
public sealed class ListPersonsHandler(IPersonRegistry persons) : IRequestHandler<ListPersonsQuery, IReadOnlyList<PersonInfo>>
{
    public async ValueTask<Result<IReadOnlyList<PersonInfo>>> HandleAsync(ListPersonsQuery query, CancellationToken ct = default)
        => Result.Success(await persons.ListAsync(ct));
}
