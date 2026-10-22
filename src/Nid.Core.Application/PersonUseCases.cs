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

/// <summary>Whether the actor owns an active private item: without one, « Moi » shows exactly « Foyer » and the selector is hidden.</summary>
public sealed record GetMyPrivateItemsQuery : IQuery<PrivateItemsView>;

public sealed record PrivateItemsView(bool Owns);

[RequestHandler<GetMyPrivateItemsQuery, PrivateItemsView>]
public sealed class GetMyPrivateItemsHandler(IVisibleItems visible, IActorAccessor accessor) : IRequestHandler<GetMyPrivateItemsQuery, PrivateItemsView>
{
    public async ValueTask<Result<PrivateItemsView>> HandleAsync(GetMyPrivateItemsQuery query, CancellationToken ct = default)
    {
        if (accessor.Current?.PersonId is not { } me)
            return Result.Success(new PrivateItemsView(false));
        // Me is Household plus the actor's private items, so a larger Me set means at least one private item.
        var mine = await visible.ForAsync(me, VisibilityScope.Me, ct);
        var household = await visible.ForAsync(me, VisibilityScope.Household, ct);
        return Result.Success(new PrivateItemsView(mine.Count > household.Count));
    }
}

[RequestHandler<ListPersonsQuery, IReadOnlyList<PersonInfo>>]
public sealed class ListPersonsHandler(IPersonRegistry persons) : IRequestHandler<ListPersonsQuery, IReadOnlyList<PersonInfo>>
{
    public async ValueTask<Result<IReadOnlyList<PersonInfo>>> HandleAsync(ListPersonsQuery query, CancellationToken ct = default)
        => Result.Success(await persons.ListAsync(ct));
}
