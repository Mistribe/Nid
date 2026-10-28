using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Core.Application;

/// <summary><paramref name="IsAdmin"/>: whether the identity provider put this adult in the admin group.</summary>
public sealed record ProvisionAdultCommand(string OidcSubject, string DisplayName, string? Email, bool IsAdmin = false) : ICommand<PersonInfo>;

public sealed record CreateDependentCommand(string DisplayName) : ICommand<PersonInfo>;

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
