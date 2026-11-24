using System.Globalization;
using System.Text.Json.Serialization;
using Nid.Core.Contracts;
using Nid.Platform.Contracts;
using NodaTime;
using NodaTime.Text;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;
using UnambitiousFx.Synapse.Abstractions;

namespace Nid.Core.Application;

// Issue 517: the writes of a local sign-in (local-accounts.md §5, §7, §9, §10). No session exists yet, so the host runs them
// as the system, as it does an OIDC first sign-in; the password and the codes are checked by the host, which keeps the hasher
// and the TOTP secrets. Browser-only: a token or the assistant never reaches an account's lock or its codes.

/// <summary>
/// A wrong password or code counts towards the account's lock (spec §9): at <paramref name="LockThreshold"/> failures in a row it
/// locks for 1 minute × 2^(level − 1), at most <paramref name="LockMax"/>, and the lock is a <c>login_locked</c> event. The host
/// passes its bounded <c>Auth:Local</c> settings.
/// </summary>
public sealed record RecordLoginFailureCommand(PersonId Person, int LockThreshold, NodaTime.Duration LockMax)
    : ICommand<FailureOutcome>, IBrowserOnly, IRecordsSecurityEvent
{
    public static readonly NodaTime.Duration LockBase = NodaTime.Duration.FromMinutes(1);

    public SecurityEvent? EventFor(object response)
        => response is FailureOutcome { LockedNow: true, Until: { } until } outcome
            ? new SecurityEvent(SecurityEventNames.LoginLocked, Person, [], new Dictionary<string, string>
            {
                ["level"] = outcome.Level.ToString(CultureInfo.InvariantCulture), ["until"] = InstantPattern.ExtendedIso.Format(until),
            })
            : null;
}

/// <summary>A sign-in passed: the failures, the level and the lock are cleared; <paramref name="Rehash"/>, when set, replaces the hash.</summary>
public sealed record RecordLoginSuccessCommand(PersonId Person, [property: JsonIgnore] string? Rehash) : ICommand<PersonId>, IBrowserOnly;

/// <summary>The password was right but a code is still asked (spec §10): only the hash is replaced, the failures keep counting.</summary>
public sealed record RehashPasswordCommand(PersonId Person, [property: JsonIgnore] string PasswordHash) : ICommand<PersonId>, IBrowserOnly;

/// <summary>What the code step of a sign-in used: a TOTP code (<c>null</c>), or a recovery code and how many are left.</summary>
public sealed record TwoStepPassed(PersonId Person, int? RecoveryCodesLeft);

/// <summary>
/// The code step of a sign-in (spec §10): the TOTP <paramref name="Step"/> the host matched the code to, or the hash of a recovery
/// code. A step used already (a replay), a recovery code that matches none, or neither is <see cref="LocalAccountCodes.InvalidCode"/>.
/// A pass clears the failures as <see cref="RecordLoginSuccessCommand"/> does; a recovery code is a <c>recovery_code_used</c> event.
/// </summary>
public sealed record CompleteTwoStepCommand(PersonId Person, long? Step, [property: JsonIgnore] string? RecoveryCodeHash)
    : ICommand<TwoStepPassed>, IBrowserOnly, IRecordsSecurityEvent
{
    public SecurityEvent? EventFor(object response)
        => response is TwoStepPassed { RecoveryCodesLeft: { } left }
            ? new SecurityEvent(SecurityEventNames.RecoveryCodeUsed, Person, [],
                new Dictionary<string, string> { ["left"] = left.ToString(CultureInfo.InvariantCulture) })
            : null;
}

/// <summary>
/// The setup of a fresh instance (spec §5): the first adult, admin, with their account. The host has checked the setup token and
/// the rules, and hashed the password. <see cref="LocalAccountCodes.SetupClosed"/> once an adult exists.
/// </summary>
public sealed record CompleteSetupCommand(string DisplayName, string Login, [property: JsonIgnore] string PasswordHash)
    : ICommand<PersonInfo>, IBrowserOnly, IRecordsSecurityEvent
{
    public SecurityEvent EventFor(object response) => SecurityEvent.Of(SecurityEventNames.SetupCompleted, ((PersonInfo)response).Id);
}

/// <summary>
/// Uses a one-time password link (spec §7): an <c>enroll</c> link creates the account with <paramref name="Login"/>, a <c>reset</c>
/// link replaces the password and closes the adult's sessions. Not found when the link is not live.
/// </summary>
public sealed record CompletePasswordLinkCommand([property: JsonIgnore] string Token, string? Login, [property: JsonIgnore] string PasswordHash)
    : ICommand<CompletedLink>, IBrowserOnly, IRecordsSecurityEvent
{
    public SecurityEvent EventFor(object response)
    {
        var done = (CompletedLink)response;
        var name = done.Purpose == CredentialLinkPurposes.Enroll ? SecurityEventNames.AccountEnrolled : SecurityEventNames.PasswordReset;
        return new SecurityEvent(name, done.Person.Id, [], new Dictionary<string, string> { ["link_id"] = done.LinkId.ToString() });
    }
}

internal static class SignInActor
{
    /// <summary>The system signs anyone in; an adult in session only counts against their own account.</summary>
    public static bool May(IActorAccessor accessor, PersonId person)
        => accessor.Current is { } actor && (actor.Kind == ActorKind.System || actor.PersonId == person);

    public static Result<T> Refused<T>() where T : notnull
        => Result.Failure<T>(new UnauthorizedFailure("Only the system or the account's own adult signs in to it.", null));

    public static Result<T> InvalidCode<T>() where T : notnull
        => Result.Failure<T>(new Failure(LocalAccountCodes.InvalidCode, "This code is not the right one: check the app's current code.",
            new Dictionary<string, object?> { ["field"] = "code" }));

    /// <summary>Uses a recovery code by its hash, or accepts a TOTP step once (a replay is refused in one statement).</summary>
    public static async Task<(bool Passed, int? Left)> PassAsync(
        ILocalAccountStore accounts, PersonId person, long? step, string? recoveryCodeHash, Instant now, CancellationToken ct)
    {
        if (recoveryCodeHash is not null)
            return await accounts.UseRecoveryCodeAsync(person, recoveryCodeHash, now, ct) is { } left ? (true, left) : (false, null);
        return (step is { } s && await accounts.AcceptTotpStepAsync(person, s, ct), null);
    }
}

[RequestHandler<RecordLoginFailureCommand, FailureOutcome>]
public sealed class RecordLoginFailureHandler(ILocalAccountStore accounts, IActorAccessor actor, IClock clock)
    : IRequestHandler<RecordLoginFailureCommand, FailureOutcome>
{
    public async ValueTask<Result<FailureOutcome>> HandleAsync(RecordLoginFailureCommand command, CancellationToken ct = default)
        => SignInActor.May(actor, command.Person)
            ? Result.Success(await accounts.RecordFailureAsync(command.Person, clock.GetCurrentInstant(), command.LockThreshold,
                RecordLoginFailureCommand.LockBase, command.LockMax, ct))
            : SignInActor.Refused<FailureOutcome>();
}

[RequestHandler<RecordLoginSuccessCommand, PersonId>]
public sealed class RecordLoginSuccessHandler(ILocalAccountStore accounts, IActorAccessor actor, IClock clock)
    : IRequestHandler<RecordLoginSuccessCommand, PersonId>
{
    public async ValueTask<Result<PersonId>> HandleAsync(RecordLoginSuccessCommand command, CancellationToken ct = default)
    {
        if (!SignInActor.May(actor, command.Person))
            return SignInActor.Refused<PersonId>();
        await accounts.RecordSuccessAsync(command.Person, clock.GetCurrentInstant(), command.Rehash, ct);
        return Result.Success(command.Person);
    }
}

[RequestHandler<RehashPasswordCommand, PersonId>]
public sealed class RehashPasswordHandler(ILocalAccountStore accounts, IActorAccessor actor) : IRequestHandler<RehashPasswordCommand, PersonId>
{
    public async ValueTask<Result<PersonId>> HandleAsync(RehashPasswordCommand command, CancellationToken ct = default)
    {
        if (!SignInActor.May(actor, command.Person))
            return SignInActor.Refused<PersonId>();
        await accounts.RehashAsync(command.Person, command.PasswordHash, ct);
        return Result.Success(command.Person);
    }
}

[RequestHandler<CompleteTwoStepCommand, TwoStepPassed>]
public sealed class CompleteTwoStepHandler(ILocalAccountStore accounts, IActorAccessor actor, IClock clock)
    : IRequestHandler<CompleteTwoStepCommand, TwoStepPassed>
{
    public async ValueTask<Result<TwoStepPassed>> HandleAsync(CompleteTwoStepCommand command, CancellationToken ct = default)
    {
        if (!SignInActor.May(actor, command.Person))
            return SignInActor.Refused<TwoStepPassed>();
        var now = clock.GetCurrentInstant();
        var (passed, left) = await SignInActor.PassAsync(accounts, command.Person, command.Step, command.RecoveryCodeHash, now, ct);
        if (!passed)
            return SignInActor.InvalidCode<TwoStepPassed>();
        await accounts.RecordSuccessAsync(command.Person, now, null, ct);
        return Result.Success(new TwoStepPassed(command.Person, left));
    }
}

[RequestHandler<CompleteSetupCommand, PersonInfo>]
public sealed class CompleteSetupHandler(ILocalAccountStore accounts) : IRequestHandler<CompleteSetupCommand, PersonInfo>
{
    public async ValueTask<Result<PersonInfo>> HandleAsync(CompleteSetupCommand command, CancellationToken ct = default)
        => await accounts.CreateFirstAdminAsync(command.DisplayName, command.Login, command.PasswordHash, ct);
}

[RequestHandler<CompletePasswordLinkCommand, CompletedLink>]
public sealed class CompletePasswordLinkHandler(ILocalAccountStore accounts, IClock clock) : IRequestHandler<CompletePasswordLinkCommand, CompletedLink>
{
    public async ValueTask<Result<CompletedLink>> HandleAsync(CompletePasswordLinkCommand command, CancellationToken ct = default)
        => await accounts.CompleteLinkAsync(command.Token, command.Login, command.PasswordHash, clock.GetCurrentInstant(), ct);
}
