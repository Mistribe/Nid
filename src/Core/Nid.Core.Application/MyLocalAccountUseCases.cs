using System.Text.Json.Serialization;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Core.Application;

/// <summary>Issue 215: what the Security page shows of the adult's own local account; never the hash.</summary>
public sealed record MyCredentialView(string Login, Instant PasswordChangedAt, bool TotpEnabled, int RecoveryCodesLeft);

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
        var left = credential.TotpEnabled ? await accounts.RecoveryCodesLeftAsync(me, ct) : 0;
        return Result.Success(new MyCredentialView(credential.Login, credential.PasswordChangedAt, credential.TotpEnabled, left));
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

/// <summary>
/// Issue 215, local-accounts.md §10: keeps a new TOTP secret, encrypted by the host, waiting for its first code. Conflict
/// when the second factor is already on.
/// </summary>
public sealed record BeginMyTotpCommand([property: JsonIgnore] string ProtectedSecret) : ICommand<PersonId>, IBrowserOnly;

/// <summary>
/// The first code was right (the host checked it against the waiting secret): the second factor is on, with these recovery
/// codes, kept by their hashes. Not found when no secret waits any more.
/// </summary>
public sealed record ConfirmMyTotpCommand(long Step, [property: JsonIgnore] IReadOnlyCollection<string> RecoveryCodeHashes, Instant PendingSince)
    : ICommand<PersonId>, IBrowserOnly, IRecordsSecurityEvent
{
    public SecurityEvent EventFor(object response) => SecurityEvent.Of(SecurityEventNames.TotpEnabled, (PersonId)response);
}

/// <summary>The adult turns their second factor off; the host has checked their password and a code.</summary>
public sealed record DisableMyTotpCommand : ICommand<PersonInfo>, IBrowserOnly, IRecordsSecurityEvent
{
    public SecurityEvent EventFor(object response) => SecurityEvent.Of(SecurityEventNames.TotpDisabled, ((PersonInfo)response).Id);
}

/// <summary>New recovery codes replace the previous ones; the host has checked the password. Not found with the factor off.</summary>
public sealed record RegenerateMyRecoveryCodesCommand([property: JsonIgnore] IReadOnlyCollection<string> CodeHashes)
    : ICommand<PersonId>, IBrowserOnly, IRecordsSecurityEvent
{
    public SecurityEvent EventFor(object response) => SecurityEvent.Of(SecurityEventNames.RecoveryCodesRegenerated, (PersonId)response);
}

[RequestHandler<BeginMyTotpCommand, PersonId>]
public sealed class BeginMyTotpHandler(ILocalAccountStore accounts, IActorAccessor actor, IClock clock) : IRequestHandler<BeginMyTotpCommand, PersonId>
{
    public async ValueTask<Result<PersonId>> HandleAsync(BeginMyTotpCommand command, CancellationToken ct = default)
    {
        if (actor.Current?.PersonId is not { } me)
            return Result.Failure<PersonId>(new NotFoundFailure("account", "me"));
        return await accounts.BeginTotpAsync(me, command.ProtectedSecret, clock.GetCurrentInstant(), ct)
            ? Result.Success(me)
            : Result.Failure<PersonId>(new Failure(LocalAccountCodes.TotpAlreadyEnabled, "Two-step verification is already on.", null));
    }
}

[RequestHandler<ConfirmMyTotpCommand, PersonId>]
public sealed class ConfirmMyTotpHandler(ILocalAccountStore accounts, IActorAccessor actor, IClock clock) : IRequestHandler<ConfirmMyTotpCommand, PersonId>
{
    public async ValueTask<Result<PersonId>> HandleAsync(ConfirmMyTotpCommand command, CancellationToken ct = default)
        => actor.Current?.PersonId is { } me
           && await accounts.ConfirmTotpAsync(me, command.Step, command.RecoveryCodeHashes, clock.GetCurrentInstant(), command.PendingSince, ct)
            ? Result.Success(me)
            : Result.Failure<PersonId>(new NotFoundFailure("totp", "me"));
}

[RequestHandler<DisableMyTotpCommand, PersonInfo>]
public sealed class DisableMyTotpHandler(ILocalAccountStore accounts, IActorAccessor actor) : IRequestHandler<DisableMyTotpCommand, PersonInfo>
{
    public async ValueTask<Result<PersonInfo>> HandleAsync(DisableMyTotpCommand command, CancellationToken ct = default)
        => actor.Current?.PersonId is { } me
            ? await accounts.DisableTotpAsync(me, false, ct)
            : Result.Failure<PersonInfo>(new NotFoundFailure("account", "me"));
}

[RequestHandler<RegenerateMyRecoveryCodesCommand, PersonId>]
public sealed class RegenerateMyRecoveryCodesHandler(ILocalAccountStore accounts, IActorAccessor actor, IClock clock)
    : IRequestHandler<RegenerateMyRecoveryCodesCommand, PersonId>
{
    public async ValueTask<Result<PersonId>> HandleAsync(RegenerateMyRecoveryCodesCommand command, CancellationToken ct = default)
        => actor.Current?.PersonId is { } me && await accounts.ReplaceRecoveryCodesAsync(me, command.CodeHashes, clock.GetCurrentInstant(), ct)
            ? Result.Success(me)
            : Result.Failure<PersonId>(new NotFoundFailure("totp", "me"));
}
