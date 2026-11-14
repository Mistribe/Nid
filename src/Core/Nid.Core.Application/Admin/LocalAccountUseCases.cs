using System.Text.Json.Serialization;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Core.Application.Admin;

/// <summary>
/// A one-time password link just created (local-accounts.md §7): the secret, shown once, and when it stops working. The
/// host turns <c>Token</c> into the address <c>{PUBLIC_URL}/password#…</c>; it never reaches a log or the audit.
/// </summary>
public sealed record PasswordLinkView(Guid PersonId, string Purpose, Instant ExpiresAt, [property: JsonIgnore] string Token, [property: JsonIgnore] Guid LinkId);

/// <summary>
/// Issue 215: an admin (or `nid admin reset-password`, `create-admin`) gives an adult a link to choose their password:
/// <c>enroll</c> without a local account, <c>reset</c> with one. It revokes the adult's previous link; with
/// <paramref name="RevokeTokens"/>, the adult's access tokens too.
/// </summary>
public sealed record CreatePasswordLinkCommand(PersonId Person, NodaTime.Duration Lifetime, bool RevokeTokens = false)
    : ICommand<PasswordLinkView>, IAdminOnly, IRecordsSecurityEvent
{
    public SecurityEvent EventFor(object response)
    {
        var link = (PasswordLinkView)response;
        return new SecurityEvent(SecurityEventNames.CredentialLinkIssued, Person, [],
            new Dictionary<string, string> { ["purpose"] = link.Purpose, ["link_id"] = link.LinkId.ToString() });
    }
}

public sealed record RevokePasswordLinkCommand(PersonId Person) : ICommand<PersonId>, IAdminOnly, IRecordsSecurityEvent
{
    public SecurityEvent EventFor(object response) => SecurityEvent.Of(SecurityEventNames.CredentialLinkRevoked, Person);
}

public sealed record UnlockAccountCommand(PersonId Person) : ICommand<PersonId>, IAdminOnly, IRecordsSecurityEvent
{
    public SecurityEvent EventFor(object response) => SecurityEvent.Of(SecurityEventNames.AccountUnlocked, Person);
}

/// <summary>`nid admin create-admin`: a new adult who is admin, still pending until they use their link.</summary>
public sealed record CreateAdminAdultCommand(string DisplayName, string? Email) : ICommand<AdultView>, IAdminOnly, IRecordsSecurityEvent
{
    public SecurityEvent EventFor(object response)
        => new(SecurityEventNames.AdminGranted, new PersonId(((AdultView)response).Id), [],
            new Dictionary<string, string> { ["source"] = "admin-cli" });
}

[RequestHandler<CreatePasswordLinkCommand, PasswordLinkView>]
public sealed class CreatePasswordLinkHandler(
    ILocalAccountStore accounts, IPersonRegistry persons, IPersonalAccessTokens tokens, IActorAccessor actor)
    : IRequestHandler<CreatePasswordLinkCommand, PasswordLinkView>
{
    public async ValueTask<Result<PasswordLinkView>> HandleAsync(CreatePasswordLinkCommand command, CancellationToken ct = default)
    {
        if (await persons.FindAsync(command.Person, ct) is not { Kind: PersonKind.Adult })
            return Result.Failure<PasswordLinkView>(new NotFoundFailure("person", command.Person.ToString()));
        var current = actor.Current;
        var via = current?.Via == ActorOrigin.AdminCli ? "admin-cli" : "ui";
        var created = await accounts.CreateLinkAsync(command.Person, current?.PersonId, via, command.Lifetime, ct);
        if (!created.TryGetValue(out var link))
            return Result.Failure<PasswordLinkView>(created.TryGetFailure(out var failure) ? failure! : new Failure("ERROR", "The link was not created.", null));
        if (command.RevokeTokens)
            await tokens.RevokeAllAsync(command.Person, ct);
        return Result.Success(new PasswordLinkView(command.Person.Value, link!.Purpose, link.ExpiresAt, link.Token, link.Id));
    }
}

[RequestHandler<RevokePasswordLinkCommand, PersonId>]
public sealed class RevokePasswordLinkHandler(ILocalAccountStore accounts) : IRequestHandler<RevokePasswordLinkCommand, PersonId>
{
    public async ValueTask<Result<PersonId>> HandleAsync(RevokePasswordLinkCommand command, CancellationToken ct = default)
        => await accounts.RevokeLinkAsync(command.Person, ct)
            ? Result.Success(command.Person)
            : Result.Failure<PersonId>(new NotFoundFailure("link", command.Person.ToString()));
}

[RequestHandler<UnlockAccountCommand, PersonId>]
public sealed class UnlockAccountHandler(ILocalAccountStore accounts) : IRequestHandler<UnlockAccountCommand, PersonId>
{
    public async ValueTask<Result<PersonId>> HandleAsync(UnlockAccountCommand command, CancellationToken ct = default)
        => await accounts.UnlockAsync(command.Person, ct)
            ? Result.Success(command.Person)
            : Result.Failure<PersonId>(new NotFoundFailure("account", command.Person.ToString()));
}

[RequestHandler<CreateAdminAdultCommand, AdultView>]
public sealed class CreateAdminAdultHandler(ILocalAccountStore accounts) : IRequestHandler<CreateAdminAdultCommand, AdultView>
{
    public async ValueTask<Result<AdultView>> HandleAsync(CreateAdminAdultCommand command, CancellationToken ct = default)
        => (await accounts.CreateAdminAdultAsync(command.DisplayName, command.Email, ct)).Map(p => AdultView.From(p));
}

/// <summary>The admin role after a change: the adult as the list shows them, and whether anything moved.</summary>
public sealed record AdminRoleView(AdultView Adult, bool Changed);

/// <summary>
/// Issue 215 (local-accounts.md §6): with `Auth:AdminSource=nid`, an admin (or `nid admin grant-admin` / `revoke-admin`)
/// gives or takes away the admin role; the last admin keeps it. <paramref name="Source"/>: <c>nid</c> or <c>admin-cli</c>.
/// The host refuses the command when the identity provider decides the role.
/// </summary>
public sealed record SetAdminCommand(PersonId Person, bool Admin, string Source = "nid") : ICommand<AdminRoleView>, IAdminOnly, IRecordsSecurityEvent
{
    public SecurityEvent? EventFor(object response)
        => response is AdminRoleView { Changed: true }
            ? new SecurityEvent(Admin ? SecurityEventNames.AdminGranted : SecurityEventNames.AdminRevoked, Person, [],
                new Dictionary<string, string> { ["source"] = Source })
            : null;
}

[RequestHandler<SetAdminCommand, AdminRoleView>]
public sealed class SetAdminHandler(IPersonRegistry persons) : IRequestHandler<SetAdminCommand, AdminRoleView>
{
    public async ValueTask<Result<AdminRoleView>> HandleAsync(SetAdminCommand command, CancellationToken ct = default)
        => (await persons.SetAdminAsync(command.Person, command.Admin, ct)).Map(change => new AdminRoleView(AdultView.From(change.Person), change.Changed));
}
