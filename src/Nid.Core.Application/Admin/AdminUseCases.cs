using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Core.Application.Admin;

/// <summary>
/// An adult as the admin area shows it (administration-and-audit.md §2.2): no item, no amount. <c>IsAdmin</c> is the
/// identity provider's answer at the adult's last sign-in; Nid does not edit it.
/// </summary>
public sealed record AdultView(Guid Id, string DisplayName, string? Email, string Status, bool IsAdmin)
{
    public static AdultView From(PersonInfo person)
        => new(person.Id.Value, person.DisplayName, person.Email, person.IsPending ? "pending" : "active", person.IsAdmin);
}

public sealed record HouseholdSettingsView(
    int MaxAdults, string ReferenceCurrency, int AdultCount, bool PricesEnabled, bool IrlEnabled, bool BankingEnabled, string? EnableBankingAppId, int? BankRetentionMonths)
{
    public static HouseholdSettingsView From(HouseholdSettingsInfo settings)
        => new(settings.MaxAdults, settings.ReferenceCurrency.Code, settings.AdultCount, settings.PricesEnabled, settings.IrlEnabled,
            settings.BankingEnabled, settings.EnableBankingAppId, settings.BankRetentionMonths);
}

public sealed record ListAdultsQuery : IQuery<IReadOnlyList<AdultView>>, IAdminOnly;

public sealed record PreRegisterAdultCommand(string DisplayName, string Email) : ICommand<AdultView>, IAdminOnly, IRecordsSecurityEvent
{
    public SecurityEvent EventFor(object response)
        => SecurityEvent.Of(SecurityEventNames.PersonPreregistered, new PersonId(((AdultView)response).Id));
}

public sealed record DeletePendingAdultCommand(PersonId Person) : ICommand<PersonId>, IAdminOnly, IRecordsSecurityEvent
{
    public SecurityEvent EventFor(object response) => SecurityEvent.Of(SecurityEventNames.PersonPreregistrationCancelled, Person);
}

public sealed record RevokeSessionsCommand(PersonId Person) : ICommand<AdultView>, IAdminOnly, IRecordsSecurityEvent
{
    public SecurityEvent EventFor(object response) => SecurityEvent.Of(SecurityEventNames.SessionRevoked, Person);
}

public sealed record TokensRevokedView(Guid PersonId, int Count);

/// <summary>The rescue for a person's tokens (spec 4A §9): run by `nid admin revoke-tokens`.</summary>
public sealed record RevokeTokensCommand(PersonId Person) : ICommand<TokensRevokedView>, IAdminOnly, IRecordsSecurityEvent
{
    public SecurityEvent? EventFor(object response)
        => ((TokensRevokedView)response).Count == 0 ? null : new(SecurityEventNames.TokenRevoked, Person, [],
            new Dictionary<string, string> { ["count"] = ((TokensRevokedView)response).Count.ToString(System.Globalization.CultureInfo.InvariantCulture) });
}

public sealed record GetHouseholdSettingsQuery : IQuery<HouseholdSettingsView>, IAdminOnly;

public sealed record UpdateHouseholdSettingsCommand(
    int? MaxAdults, string? ReferenceCurrency, bool? PricesEnabled, bool? IrlEnabled = null, bool? BankingEnabled = null, string? EnableBankingAppId = null,
    int? BankRetentionMonths = null, bool ClearBankRetention = false)
    : ICommand<HouseholdSettingsView>, IAdminOnly, IRecordsSecurityEvent
{
    public SecurityEvent? EventFor(object response)
    {
        var fields = new[] { MaxAdults is null ? null : "max_adults", ReferenceCurrency is null ? null : "reference_currency", PricesEnabled is null ? null : "prices_enabled", IrlEnabled is null ? null : "irl_enabled",
            BankingEnabled is null ? null : "banking_enabled", EnableBankingAppId is null ? null : "enable_banking_app_id",
            BankRetentionMonths is null && !ClearBankRetention ? null : "bank_retention_months" };
        var joined = string.Join(",", fields.OfType<string>());
        // No field was given (Minor #4): nothing changed, so nothing is worth an event.
        return joined.Length == 0 ? null : new SecurityEvent(SecurityEventNames.HouseholdSettingsChanged, null, [],
            new Dictionary<string, string> { ["fields"] = joined });
    }
}

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

[RequestHandler<RevokeSessionsCommand, AdultView>]
public sealed class RevokeSessionsHandler(IPersonRegistry persons) : IRequestHandler<RevokeSessionsCommand, AdultView>
{
    public async ValueTask<Result<AdultView>> HandleAsync(RevokeSessionsCommand command, CancellationToken ct = default)
        => (await persons.RevokeSessionsAsync(command.Person, ct)).Map(AdultView.From);
}

[RequestHandler<RevokeTokensCommand, TokensRevokedView>]
public sealed class RevokeTokensHandler(IPersonRegistry persons, IPersonalAccessTokens tokens) : IRequestHandler<RevokeTokensCommand, TokensRevokedView>
{
    public async ValueTask<Result<TokensRevokedView>> HandleAsync(RevokeTokensCommand command, CancellationToken ct = default)
    {
        var person = await persons.FindAsync(command.Person, ct);
        if (person is null || person.Kind != PersonKind.Adult)
            return Result.Failure<TokensRevokedView>(new UnambitiousFx.Functional.Failures.NotFoundFailure("person", command.Person.ToString()));
        return Result.Success(new TokensRevokedView(command.Person.Value, await tokens.RevokeAllAsync(command.Person, ct)));
    }
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
        => (await settings.UpdateAsync(new HouseholdSettingsChange(
                command.MaxAdults, command.ReferenceCurrency, command.PricesEnabled, command.IrlEnabled, command.BankingEnabled, command.EnableBankingAppId,
                command.BankRetentionMonths, command.ClearBankRetention), ct))
            .Map(HouseholdSettingsView.From);
}

/// <summary>A person as `nid admin list-persons` shows it (1F4 design §3): whether a subject is bound, never its value.</summary>
public sealed record PersonView(Guid Id, string Kind, string DisplayName, string? Email, string Status, bool IsAdmin, bool HasSubject)
{
    public static PersonView From(PersonInfo person) => new(
        person.Id.Value, person.Kind == PersonKind.Adult ? "adult" : "dependent", person.DisplayName, person.Email,
        person.IsPending ? "pending" : "active", person.IsAdmin, person.Kind == PersonKind.Adult && !person.IsPending);
}

public sealed record ListAllPersonsQuery : IQuery<IReadOnlyList<PersonView>>, IAdminOnly;

public sealed record RebindSubjectCommand(PersonId Person, string Subject) : ICommand<RebindOutcome>, IAdminOnly, IRecordsSecurityEvent
{
    public SecurityEvent? EventFor(object response) => response is RebindOutcome { Changed: true } outcome
        ? new SecurityEvent(SecurityEventNames.SubjectRebound, Person, [],
            new Dictionary<string, string> { ["had_subject"] = outcome.PreviousSubject is null ? "false" : "true" })
        : null;
}

[RequestHandler<ListAllPersonsQuery, IReadOnlyList<PersonView>>]
public sealed class ListAllPersonsHandler(IPersonRegistry persons) : IRequestHandler<ListAllPersonsQuery, IReadOnlyList<PersonView>>
{
    public async ValueTask<Result<IReadOnlyList<PersonView>>> HandleAsync(ListAllPersonsQuery query, CancellationToken ct = default)
        => Result.Success<IReadOnlyList<PersonView>>((await persons.ListAsync(ct)).Select(PersonView.From).ToList());
}

[RequestHandler<RebindSubjectCommand, RebindOutcome>]
public sealed class RebindSubjectHandler(IPersonRegistry persons) : IRequestHandler<RebindSubjectCommand, RebindOutcome>
{
    public async ValueTask<Result<RebindOutcome>> HandleAsync(RebindSubjectCommand command, CancellationToken ct = default)
        => await persons.RebindSubjectAsync(command.Person, command.Subject, ct);
}
