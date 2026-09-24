using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Core.Application.Admin;

/// <summary>An adult as the admin area shows it (administration-and-audit.md §2.2): no item, no amount.</summary>
public sealed record AdultView(Guid Id, string DisplayName, string? Email, string Status, bool IsAdmin)
{
    public static AdultView From(PersonInfo person)
        => new(person.Id.Value, person.DisplayName, person.Email, person.IsPending ? "pending" : "active", person.IsAdmin);
}

public sealed record HouseholdSettingsView(int MaxAdults, string ReferenceCurrency, int AdultCount)
{
    public static HouseholdSettingsView From(HouseholdSettingsInfo settings)
        => new(settings.MaxAdults, settings.ReferenceCurrency.Code, settings.AdultCount);
}

public sealed record ListAdultsQuery : IQuery<IReadOnlyList<AdultView>>, IAdminOnly;

public sealed record PreRegisterAdultCommand(string DisplayName, string Email) : ICommand<AdultView>, IAdminOnly;

public sealed record DeletePendingAdultCommand(PersonId Person) : ICommand<PersonId>, IAdminOnly;

public sealed record SetAdminCommand(PersonId Person, bool IsAdmin) : ICommand<AdultView>, IAdminOnly;

public sealed record RevokeSessionsCommand(PersonId Person) : ICommand<AdultView>, IAdminOnly;

public sealed record GetHouseholdSettingsQuery : IQuery<HouseholdSettingsView>, IAdminOnly;

public sealed record UpdateHouseholdSettingsCommand(int? MaxAdults, string? ReferenceCurrency) : ICommand<HouseholdSettingsView>, IAdminOnly;

[RequestHandler<ListAdultsQuery, IReadOnlyList<AdultView>>]
public sealed class ListAdultsHandler(IPersonRegistry persons) : IRequestHandler<ListAdultsQuery, IReadOnlyList<AdultView>>
{
    public async ValueTask<Result<IReadOnlyList<AdultView>>> HandleAsync(ListAdultsQuery query, CancellationToken ct = default)
        => Result.Success<IReadOnlyList<AdultView>>(
            (await persons.ListAsync(ct)).Where(p => p.Kind == PersonKind.Adult).Select(AdultView.From).ToList());
}

[RequestHandler<PreRegisterAdultCommand, AdultView>]
public sealed class PreRegisterAdultHandler(IPersonRegistry persons) : IRequestHandler<PreRegisterAdultCommand, AdultView>
{
    public async ValueTask<Result<AdultView>> HandleAsync(PreRegisterAdultCommand command, CancellationToken ct = default)
        => (await persons.PreRegisterAdultAsync(command.DisplayName, command.Email, ct)).Map(AdultView.From);
}

[RequestHandler<DeletePendingAdultCommand, PersonId>]
public sealed class DeletePendingAdultHandler(IPersonRegistry persons) : IRequestHandler<DeletePendingAdultCommand, PersonId>
{
    public async ValueTask<Result<PersonId>> HandleAsync(DeletePendingAdultCommand command, CancellationToken ct = default)
    {
        var deleted = await persons.DeletePendingAdultAsync(command.Person, ct);
        return deleted.TryGetFailure(out var failure) ? Result.Failure<PersonId>(failure!) : Result.Success(command.Person);
    }
}

[RequestHandler<SetAdminCommand, AdultView>]
public sealed class SetAdminHandler(IPersonRegistry persons) : IRequestHandler<SetAdminCommand, AdultView>
{
    public async ValueTask<Result<AdultView>> HandleAsync(SetAdminCommand command, CancellationToken ct = default)
        => (await persons.SetAdminAsync(command.Person, command.IsAdmin, ct)).Map(AdultView.From);
}

[RequestHandler<RevokeSessionsCommand, AdultView>]
public sealed class RevokeSessionsHandler(IPersonRegistry persons) : IRequestHandler<RevokeSessionsCommand, AdultView>
{
    public async ValueTask<Result<AdultView>> HandleAsync(RevokeSessionsCommand command, CancellationToken ct = default)
        => (await persons.RevokeSessionsAsync(command.Person, ct)).Map(AdultView.From);
}

[RequestHandler<GetHouseholdSettingsQuery, HouseholdSettingsView>]
public sealed class GetHouseholdSettingsHandler(IHouseholdSettings settings) : IRequestHandler<GetHouseholdSettingsQuery, HouseholdSettingsView>
{
    public async ValueTask<Result<HouseholdSettingsView>> HandleAsync(GetHouseholdSettingsQuery query, CancellationToken ct = default)
        => Result.Success(HouseholdSettingsView.From(await settings.ReadAsync(ct)));
}

[RequestHandler<UpdateHouseholdSettingsCommand, HouseholdSettingsView>]
public sealed class UpdateHouseholdSettingsHandler(IHouseholdSettings settings)
    : IRequestHandler<UpdateHouseholdSettingsCommand, HouseholdSettingsView>
{
    public async ValueTask<Result<HouseholdSettingsView>> HandleAsync(UpdateHouseholdSettingsCommand command, CancellationToken ct = default)
        => (await settings.UpdateAsync(new HouseholdSettingsChange(command.MaxAdults, command.ReferenceCurrency), ct))
            .Map(HouseholdSettingsView.From);
}
