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

[RequestHandler<ListPersonsQuery, IReadOnlyList<PersonInfo>>]
public sealed class ListPersonsHandler(IPersonRegistry persons) : IRequestHandler<ListPersonsQuery, IReadOnlyList<PersonInfo>>
{
    public async ValueTask<Result<IReadOnlyList<PersonInfo>>> HandleAsync(ListPersonsQuery query, CancellationToken ct = default)
        => Result.Success(await persons.ListAsync(ct));
}
