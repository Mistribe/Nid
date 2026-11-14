using System.Globalization;
using Nid.Core.Application;
using Nid.Core.Contracts;
using Nid.Host.Api;
using Nid.Platform.Contracts;
using UnambitiousFx.Functional.Failures;

namespace Nid.Host.Auth;

public sealed record TotpLoginBody(string? Code, string? RecoveryCode);

public sealed record TotpSetupView(string Secret, string OtpauthUri);

public sealed record TotpConfirmBody(string? Code);

public sealed record TotpDisableBody(string? Password, string? Code);

public sealed record RecoveryCodesBody(string? Password);

/// <summary>The recovery codes, shown once: Nid keeps only their hashes.</summary>
public sealed record RecoveryCodesView(IReadOnlyList<string> Codes);

/// <summary>Issue 215, local-accounts.md §10: the second factor, at sign-in and on the Security page.</summary>
public sealed partial class LocalSignIn
{
    public const string InvalidCode = "auth.invalid_code";
    public const string MfaExpired = "auth.mfa_expired";

    /// <summary>A secret drawn and not confirmed within this time is abandoned.</summary>
    public static readonly NodaTime.Duration PendingTotpLifetime = NodaTime.Duration.FromMinutes(15);

    /// <summary>
    /// The code step of a sign-in (spec §10): a TOTP code or a recovery code, after a right password. A wrong code counts
    /// towards the lock; the third on one step, or a lock, sends back to the password.
    /// </summary>
    public async Task<IResult> TotpAsync(HttpContext http, TotpLoginBody body)
    {
        if (Refused(http) is { } refused) return refused;
        var ct = http.RequestAborted;
        var now = clock.GetCurrentInstant();
        var ticket = mfa.Read(http);
        var credential = ticket is null ? null : await accounts.FindAsync(ticket.Person, ct);
        var person = credential is null ? null : await persons.FindAsync(credential.Person, ct);
        // A reset, a « sign out everywhere » or a reset-totp since the password step ends it, as does a lock.
        if (ticket is null || credential is not { TotpEnabled: true } || person is not { Kind: PersonKind.Adult }
            || person.SessionEpoch != ticket.Epoch || credential.LockedUntil is { } until && until > now)
            return Expired(http, ticket);

        var method = "password+totp";
        var passed = false;
        if (RecoveryCodes.Normalize(body.RecoveryCode) is { } recovery)
        {
            if (await accounts.UseRecoveryCodeAsync(person.Id, RecoveryCodes.Hash(recovery), now, ct) is { } left)
            {
                passed = true;
                await events.RecordAsync(new SecurityEvent(SecurityEventNames.RecoveryCodeUsed, person.Id, [],
                    new Dictionary<string, string> { ["left"] = left.ToString(CultureInfo.InvariantCulture) }), Actor.System, ct);
            }
        }
        else if (secrets.Unprotect(credential.TotpSecret) is { } secret && Totp.Match(secret, body.Code, now) is { } step)
        {
            // The same code twice, or an older one, is a replay: refused like a wrong one.
            passed = await accounts.AcceptTotpStepAsync(person.Id, step, ct);
        }

        if (!passed)
        {
            gate.Fail(TokenAuthentication.ClientAddress(http));
            var lockedNow = await CountAccountFailureAsync(person.Id, now, ct);
            await RecordFailureAsync(person.Id, "bad_code", TokenAuthentication.ClientAddress(http), ct);
            if (mfa.Fail(ticket) || lockedNow)
                return Expired(http, ticket);
            return Problem(StatusCodes.Status401Unauthorized, InvalidCode, "This code is not the right one: check the app's current code.");
        }

        await accounts.RecordSuccessAsync(person.Id, now, null, ct);
        mfa.Clear(http, ticket);
        await OpenSessionAsync(http, person, "pwd+otp", method, ct);
        return ResultHttp.Json(new PasswordLoginResult("done", ticket.ReturnUrl));
    }

    /// <summary>Draws a secret waiting for its first code (spec §10): the app reads it from the QR code or by hand.</summary>
    public async Task<IResult> BeginTotpAsync(HttpContext http)
    {
        if (await MineAsync(http) is not { } mine) return NotBrowserOrNoAccount(http);
        var secret = Totp.NewSecret();
        var begun = await invoker.InvokeAsync(new BeginMyTotpCommand(secrets.Protect(secret)), http.RequestAborted);
        if (!begun.TryGetValue(out _))
            return Failed(begun);
        return ResultHttp.Json(new TotpSetupView(Totp.Base32(secret), Totp.Uri(secret, mine.Login)));
    }

    /// <summary>The first right code turns the second factor on and hands out the recovery codes, once.</summary>
    public async Task<IResult> ConfirmTotpAsync(HttpContext http, TotpConfirmBody body)
    {
        if (await MineAsync(http) is not { } mine) return NotBrowserOrNoAccount(http);
        var now = clock.GetCurrentInstant();
        var since = now.Minus(PendingTotpLifetime);
        if (mine.TotpEnabled || mine.TotpPendingSince is not { } pending || pending < since || secrets.Unprotect(mine.TotpSecret) is not { } secret)
            return ResultHttp.Problem(new NotFoundFailure("totp", "me"));
        if (Totp.Match(secret, body.Code, now) is not { } step)
            return ResultHttp.Problem(new Failure(InvalidCode, "This code is not the right one: check the app's current code.",
                new Dictionary<string, object?> { ["field"] = "code" }));

        var codes = RecoveryCodes.New();
        var confirmed = await invoker.InvokeAsync(new ConfirmMyTotpCommand(step, Hashes(codes), since), http.RequestAborted);
        return confirmed.TryGetValue(out _) ? ResultHttp.Json(new RecoveryCodesView(codes)) : Failed(confirmed);
    }

    /// <summary>Turns the second factor off: the password and a code (or a recovery code) are asked.</summary>
    public async Task<IResult> DisableTotpAsync(HttpContext http, TotpDisableBody body)
    {
        if (await MineAsync(http) is not { } mine) return NotBrowserOrNoAccount(http);
        if (!mine.TotpEnabled)
            return ResultHttp.Problem(new NotFoundFailure("totp", "me"));
        if (await CheckMyPasswordAsync(http, mine, body.Password, "password") is { } wrong)
            return wrong;
        var now = clock.GetCurrentInstant();
        var ct = http.RequestAborted;
        var passed = RecoveryCodes.Normalize(body.Code) is { } recovery
            ? await accounts.UseRecoveryCodeAsync(mine.Person, RecoveryCodes.Hash(recovery), now, ct) is not null
            : secrets.Unprotect(mine.TotpSecret) is { } secret && Totp.Match(secret, body.Code, now) is { } step
              && await accounts.AcceptTotpStepAsync(mine.Person, step, ct);
        if (!passed)
            return ResultHttp.Problem(new Failure(InvalidCode, "This code is not the right one: check the app's current code.",
                new Dictionary<string, object?> { ["field"] = "code" }));

        var disabled = await invoker.InvokeAsync(new DisableMyTotpCommand(), ct);
        return disabled.TryGetValue(out _) ? Results.NoContent() : Failed(disabled);
    }

    /// <summary>New recovery codes, the password asked; the previous ones stop working.</summary>
    public async Task<IResult> RegenerateRecoveryCodesAsync(HttpContext http, RecoveryCodesBody body)
    {
        if (await MineAsync(http) is not { } mine) return NotBrowserOrNoAccount(http);
        if (!mine.TotpEnabled)
            return ResultHttp.Problem(new NotFoundFailure("totp", "me"));
        if (await CheckMyPasswordAsync(http, mine, body.Password, "password") is { } wrong)
            return wrong;
        var codes = RecoveryCodes.New();
        var replaced = await invoker.InvokeAsync(new RegenerateMyRecoveryCodesCommand(Hashes(codes)), http.RequestAborted);
        return replaced.TryGetValue(out _) ? ResultHttp.Json(new RecoveryCodesView(codes)) : Failed(replaced);
    }

    private static string[] Hashes(IEnumerable<string> codes) => codes.Select(c => RecoveryCodes.Hash(RecoveryCodes.Normalize(c)!)).ToArray();

    /// <summary>The adult's own local account, from a browser session only (a personal access token never reaches it).</summary>
    private async Task<LocalCredential?> MineAsync(HttpContext http)
        => actor.Current is { PersonId: { } me, TokenId: null } ? await accounts.FindAsync(me, http.RequestAborted) : null;

    private IResult NotBrowserOrNoAccount(HttpContext http)
        => actor.Current switch
        {
            null or { PersonId: null } => Results.Unauthorized(),
            { TokenId: not null } => ResultHttp.Problem(new Failure(BrowserOnlyCodes.Required, "This action is only available from a browser session.", null)),
            _ => ResultHttp.Problem(new NotFoundFailure("account", "me")),
        };

    private IResult Expired(HttpContext http, MfaTicket? ticket)
    {
        mfa.Clear(http, ticket);
        return Problem(StatusCodes.Status401Unauthorized, MfaExpired, "The sign-in took too long or too many codes were wrong: start again with the password.");
    }
}
