using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Core.Application;

/// <summary><paramref name="IsAdmin"/>: whether the identity provider put this adult in the admin group.</summary>
public sealed record ProvisionAdultCommand(string OidcSubject, string DisplayName, string? Email, bool IsAdmin = false) : ICommand<PersonInfo>;

/// <summary>#156: <paramref name="BirthDate"/> is optional, checked as <see cref="SetRetirementCommand"/>'s.</summary>
public sealed record CreateDependentCommand(string DisplayName, LocalDate? BirthDate = null) : ICommand<PersonInfo>;

/// <summary>#156: any adult renames a child, as they add one; an adult's name comes from the identity provider.</summary>
public sealed record RenameDependentCommand(PersonId Person, string DisplayName) : ICommand<PersonInfo>;

/// <summary>#156: any adult removes a child that nothing names any more (<see cref="IPersonUsePart"/>).</summary>
public sealed record DeleteDependentCommand(PersonId Person) : ICommand<PersonId>;

/// <summary>A null birth date clears it. Any adult sets any person's, a dependent's included: the household plans together.</summary>
public sealed record SetRetirementCommand(PersonId Person, LocalDate? BirthDate, int RetirementAge, int RetirementMonths = 0) : ICommand<PersonInfo>;

public sealed record ListPersonsQuery : IQuery<IReadOnlyList<PersonInfo>>;

[RequestHandler<ProvisionAdultCommand, PersonInfo>]
public sealed class ProvisionAdultHandler(IPersonRegistry persons) : IRequestHandler<ProvisionAdultCommand, PersonInfo>
{
    public async ValueTask<Result<PersonInfo>> HandleAsync(ProvisionAdultCommand command, CancellationToken ct = default)
        => await persons.ProvisionAdultAsync(command.OidcSubject, command.DisplayName, command.Email, command.IsAdmin, ct);
}

[RequestHandler<CreateDependentCommand, PersonInfo>]
public sealed class CreateDependentHandler(IPersonRegistry persons, IHouseholdCalendar calendar, IUnitOfWork unitOfWork)
    : IRequestHandler<CreateDependentCommand, PersonInfo>
{
    public async ValueTask<Result<PersonInfo>> HandleAsync(CreateDependentCommand command, CancellationToken ct = default)
    {
        if (command.BirthDate is not { } birth)
            return await persons.CreateDependentAsync(command.DisplayName, ct);
        if (await SetRetirementHandler.BirthDateRefusalAsync(birth, calendar, ct) is { } refused)
            return Result.Failure<PersonInfo>(refused);

        await using var transaction = await unitOfWork.BeginAsync(ct);
        var created = await persons.CreateDependentAsync(command.DisplayName, ct);
        if (!created.TryGetValue(out var child))
            return created;
        var dated = await persons.SetRetirementAsync(child.Id, birth, child.RetirementAge, child.RetirementMonths, ct);
        if (dated.IsSuccess)
            await transaction.CommitAsync(ct);
        return dated;
    }
}

[RequestHandler<RenameDependentCommand, PersonInfo>]
public sealed class RenameDependentHandler(IPersonRegistry persons) : IRequestHandler<RenameDependentCommand, PersonInfo>
{
    public async ValueTask<Result<PersonInfo>> HandleAsync(RenameDependentCommand command, CancellationToken ct = default)
        => await persons.RenameDependentAsync(command.Person, command.DisplayName, ct);
}

[RequestHandler<DeleteDependentCommand, PersonId>]
public sealed class DeleteDependentHandler(IPersonRegistry persons, IEnumerable<IPersonUsePart> parts, IUnitOfWork unitOfWork)
    : IRequestHandler<DeleteDependentCommand, PersonId>
{
    public async ValueTask<Result<PersonId>> HandleAsync(DeleteDependentCommand command, CancellationToken ct = default)
    {
        if (await persons.FindAsync(command.Person, ct) is not { Kind: PersonKind.Dependent } child)
            return Result.Failure<PersonId>(new NotFoundFailure("person", command.Person.ToString()));

        await using var transaction = await unitOfWork.BeginAsync(ct);
        // ponytail: count then delete, no lock — a contract or link naming the child committed in between dangles (other modules have no FK
        // to core.person). Acceptable for a household; lock the person row here and FOR SHARE in those writers if it ever bites.
        var uses = new List<string>();
        foreach (var part in parts)
            uses.AddRange((await part.UsesAsync(child.Id, ct)).Where(u => u.Count > 0).Select(u => u.Key));
        // The kinds only, never an item's name or a count: the refusal reveals nothing of another adult's private items.
        if (uses.Count > 0)
            return Result.Failure<PersonId>(new Failure(PersonCodes.PersonInUse,
                $"{child.DisplayName} is still named elsewhere: {string.Join(", ", uses)}.",
                new Dictionary<string, object?> { ["uses"] = uses }));

        var deleted = await persons.DeleteDependentAsync(child.Id, ct);
        if (deleted.TryGetFailure(out var failure))
            return Result.Failure<PersonId>(failure!);
        await transaction.CommitAsync(ct);
        return Result.Success(child.Id);
    }
}

[RequestHandler<SetRetirementCommand, PersonInfo>]
public sealed class SetRetirementHandler(IPersonRegistry persons, IHouseholdCalendar calendar) : IRequestHandler<SetRetirementCommand, PersonInfo>
{
    public const int MinAge = 50;
    public const int MaxAge = 75;
    private static readonly LocalDate Earliest = new(1900, 1, 1);

    public async ValueTask<Result<PersonInfo>> HandleAsync(SetRetirementCommand command, CancellationToken ct = default)
    {
        var errors = new List<FieldError>();
        if (command.RetirementAge is < MinAge or > MaxAge)
            errors.Add(new("retirementAge", PersonCodes.InvalidRetirementAge, $"The retirement age is between {MinAge} and {MaxAge}."));
        if (command.RetirementMonths is < 0 or > 11)
            errors.Add(new("retirementMonths", PersonCodes.InvalidRetirementAge, "The months are between 0 and 11."));
        if (errors.Count > 0)
            return Result.Failure<PersonInfo>(FieldErrors.Many(PersonCodes.InvalidRetirementAge, errors[0].Message, errors));
        if (command.BirthDate is { } birth && await BirthDateRefusalAsync(birth, calendar, ct) is { } refused)
            return Result.Failure<PersonInfo>(refused);

        return await persons.SetRetirementAsync(command.Person, command.BirthDate, command.RetirementAge, command.RetirementMonths, ct);
    }

    /// <summary>A birth date from 1900-01-01 to today, refused at <c>birthDate</c> otherwise.</summary>
    internal static async Task<Failure?> BirthDateRefusalAsync(LocalDate birth, IHouseholdCalendar calendar, CancellationToken ct)
        => birth < Earliest || birth > await calendar.TodayAsync(ct)
            ? FieldErrors.At(new Failure(PersonCodes.InvalidBirthDate, "The birth date is between 1900-01-01 and today.", null), "birthDate")
            : null;
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
