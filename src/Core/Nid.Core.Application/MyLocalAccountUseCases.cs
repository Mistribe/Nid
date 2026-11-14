using System.Text.Json.Serialization;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Core.Application;

/// <summary>Issue 215: what the Security page shows of the adult's own local account; never the hash.</summary>
public sealed record MyCredentialView(string Login, Instant PasswordChangedAt);

/// <summary>The adult's own local account; not found without one (an OIDC adult).</summary>
public sealed record GetMyCredentialQuery : IQuery<MyCredentialView>, IBrowserOnly;

/// <summary>
/// The adult replaces their own password (local-accounts.md §8). The host has checked the current password and the rules,
/// and hashed the new one; this stores it and closes the adult's other sessions. Browser sessions only.
/// </summary>
public sealed record SetMyPasswordCommand([property: JsonIgnore] string PasswordHash) : ICommand<PersonInfo>, IBrowserOnly, IRecordsSecurityEvent
{
    public SecurityEvent EventFor(object response)
        => new(SecurityEventNames.PasswordChanged, ((PersonInfo)response).Id, [], new Dictionary<string, string> { ["field"] = "password" });
}

[RequestHandler<GetMyCredentialQuery, MyCredentialView>]
public sealed class GetMyCredentialHandler(ILocalAccountStore accounts, IActorAccessor actor) : IRequestHandler<GetMyCredentialQuery, MyCredentialView>
{
    public async ValueTask<Result<MyCredentialView>> HandleAsync(GetMyCredentialQuery query, CancellationToken ct = default)
    {
        if (actor.Current?.PersonId is not { } me || await accounts.FindAsync(me, ct) is not { } credential)
            return Result.Failure<MyCredentialView>(new NotFoundFailure("account", "me"));
        return Result.Success(new MyCredentialView(credential.Login, credential.PasswordChangedAt));
    }
}

[RequestHandler<SetMyPasswordCommand, PersonInfo>]
public sealed class SetMyPasswordHandler(ILocalAccountStore accounts, IActorAccessor actor, IClock clock) : IRequestHandler<SetMyPasswordCommand, PersonInfo>
{
    public async ValueTask<Result<PersonInfo>> HandleAsync(SetMyPasswordCommand command, CancellationToken ct = default)
        => actor.Current?.PersonId is { } me
            ? await accounts.ChangePasswordAsync(me, command.PasswordHash, clock.GetCurrentInstant(), ct)
            : Result.Failure<PersonInfo>(new NotFoundFailure("account", "me"));
}
