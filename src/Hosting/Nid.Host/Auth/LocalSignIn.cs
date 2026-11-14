using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using Nid.Core.Contracts;
using Nid.Host.Api;
using Nid.Platform.Contracts;
using NodaTime;
using UnambitiousFx.Functional.Failures;

namespace Nid.Host.Auth;

public sealed record PasswordLoginBody(string? Login, string? Password, string? ReturnUrl);

public sealed record PasswordLoginResult(string Next, string? ReturnUrl);

public sealed record LocalSetupBody(string? Token, string? Name, string? Login, string? Password);

public sealed record LocalLoginView(string Login);

public sealed record PasswordLinkTokenBody(string? Token);

public sealed record PasswordLinkInfoView(string Purpose, string Name, string? Login);

public sealed record CompletePasswordLinkBody(string? Token, string? Login, string? Password);

public sealed record ChangePasswordBody(string? Current, string? New);

/// <summary>
/// The setup token of a fresh instance (local-accounts.md §5): drawn at start while no adult exists, kept in memory only,
/// written to the log; forgotten once the first administrator exists.
/// </summary>
public sealed class SetupToken
{
    public const string Prefix = "nid_setup_";
    private volatile string? value;

    public bool IsOpen => value is not null;

    public string Open() => value = Prefix + LocalSignIn.Base64Url(RandomNumberGenerator.GetBytes(32));

    public void Close() => value = null;

    public bool Matches(string? candidate)
    {
        var current = value;
        return current is not null && candidate is not null
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate), Encoding.UTF8.GetBytes(current));
    }

    /// <summary>At start, with local accounts on: an empty household opens the setup and the log gives its address.</summary>
    public static async Task AnnounceAsync(IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        if ((await scope.ServiceProvider.GetRequiredService<IPersonRegistry>().AdultIdsAsync(ct)).Count > 0)
            return;
        var token = services.GetRequiredService<SetupToken>().Open();
        var publicUrl = services.GetRequiredService<IOptions<NidAuthOptions>>().Value.PublicUrl.TrimEnd('/');
        // The token is in the fragment: the browser does not send it to the proxy, which therefore does not log it.
        logger.LogWarning("Nid has no administrator yet. Open {SetupUrl} to create the first one (or run `nid admin create-admin`).",
            $"{publicUrl}/setup#{token}");
    }
}

/// <summary>
/// local-accounts.md §9: at most one <c>login_failed</c> a minute per account (or, without one, per address), carrying how
/// many failures it stands for.
/// </summary>
public sealed class LoginFailureLog(IClock clock)
{
    private const int MaxRemembered = 10_000;
    private readonly ConcurrentDictionary<string, (long Minute, int Pending)> entries = new();

    /// <summary>The count to record now, or <c>null</c> when this minute already has its event.</summary>
    public int? Next(string key)
    {
        if (entries.Count > MaxRemembered) entries.Clear();
        var minute = clock.GetCurrentInstant().ToUnixTimeSeconds() / 60;
        int? record = null;
        entries.AddOrUpdate(key,
            _ => { record = 1; return (minute, 0); },
            (_, e) =>
            {
                if (e.Minute == minute) return (e.Minute, e.Pending + 1);
                record = e.Pending + 1;
                return (minute, 0);
            });
        return record;
    }
}

/// <summary>
/// Issue 215: what the local-account routes do (local-accounts.md §5, §7, §9, §11, §12). One route opens a local
/// session, <see cref="PasswordAsync"/>; the setup and the links never do.
/// </summary>
public sealed partial class LocalSignIn(
    ILocalAccountStore accounts, IPersonRegistry persons, LocalPasswordHasher hasher, PasswordRules rules, TokenFailureGate gate,
    LoginFailureLog failures, SetupToken setup, ISecurityEvents events, IClock clock, IOptions<NidAuthOptions> options, AuthSettings settings,
    ILogger<LocalSignIn> logger, IActorAccessor actor, UnambitiousFx.Synapse.Abstractions.IInvoker invoker, TotpSecrets secrets, MfaStep mfa)
{
    public const string InvalidCredentials = "auth.invalid_credentials";
    public const string HttpsRequired = "auth.https_required";
    public const string InvalidSetupToken = "auth.invalid_setup_token";
    public const string CurrentPasswordWrong = "auth.current_password_wrong";
    private const int MaxPasswordInput = 4 * PasswordRules.MaxLength;

    public async Task<IResult> PasswordAsync(HttpContext http, PasswordLoginBody body)
    {
        // An unsafe return URL is replaced, not refused: the sign-in never depends on it (no open redirect either way).
        var target = ReturnUrl.IsSafe(body.ReturnUrl) ? body.ReturnUrl! : "/";
        if (Refused(http) is { } refused) return refused;

        var address = TokenAuthentication.ClientAddress(http);
        var login = PasswordRules.NormalizeLogin(body.Login);
        var password = PasswordRules.NormalizePassword(body.Password ?? "");
        var ct = http.RequestAborted;
        var credential = login.Length is 0 or > PasswordRules.LoginMaxLength ? null : await accounts.FindByLoginAsync(login, ct);
        var now = clock.GetCurrentInstant();
        var locked = credential?.LockedUntil is { } until && until > now;

        // Always one hash, whatever the identifier: the answer takes as long for an unknown one (spec §3.1).
        var check = await hasher.VerifyAsync(password.Length > MaxPasswordInput ? null : credential?.PasswordHash, password, ct);
        if (check is null) return Busy();

        if (credential is null || locked || check == PasswordCheck.Failed)
        {
            gate.Fail(address);
            var reason = credential is null ? "unknown_login" : locked ? "locked" : "bad_password";
            if (credential is not null && !locked)
                await CountAccountFailureAsync(credential.Person, now, ct);
            await RecordFailureAsync(credential?.Person, reason, address, ct);
            return Problem(StatusCodes.Status401Unauthorized, InvalidCredentials,
                "Wrong identifier or password, or too many attempts: try again in a few minutes.");
        }

        if (await persons.FindAsync(credential.Person, ct) is not { Kind: PersonKind.Adult } person)
            return Problem(StatusCodes.Status401Unauthorized, InvalidCredentials, "Wrong identifier or password, or too many attempts: try again in a few minutes.");

        var rehash = check == PasswordCheck.SuccessRehashNeeded ? await hasher.HashAsync(password, ct) : null;
        if (credential.TotpEnabled)
        {
            // The failures are not cleared yet: wrong codes keep counting towards the lock (spec §10).
            if (rehash is not null) await accounts.RehashAsync(person.Id, rehash, ct);
            mfa.Issue(http, person, target);
            return ResultHttp.Json(new PasswordLoginResult("totp", null));
        }
        await accounts.RecordSuccessAsync(person.Id, now, rehash, ct);
        await OpenSessionAsync(http, person, "pwd", "password", ct);
        return ResultHttp.Json(new PasswordLoginResult("done", target));
    }

    private async Task OpenSessionAsync(HttpContext http, PersonInfo person, string amr, string method, CancellationToken ct)
    {
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            SessionClaims.Create(person, SessionClaims.LocalSubject, CookieAuthenticationDefaults.AuthenticationScheme, amr),
            new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(options.Value.SessionAbsoluteDays) });
        LoginCsrf.Clear(http, settings);
        await events.RecordAsync(new SecurityEvent(SecurityEventNames.Login, person.Id, [],
            new Dictionary<string, string> { ["method"] = method }), Actor.Browser(person.Id, person.IsAdmin), ct);
    }

    /// <summary>A wrong password or code counts towards the account's lock (spec §9); the lock itself is an event.</summary>
    private async Task<bool> CountAccountFailureAsync(PersonId person, Instant now, CancellationToken ct)
    {
        var local = options.Value.Local.Bounded();
        var outcome = await accounts.RecordFailureAsync(person, now, local.LockThreshold,
            NodaTime.Duration.FromMinutes(1), NodaTime.Duration.FromMinutes(local.LockMaxMinutes), ct);
        if (outcome.LockedNow)
            await events.RecordAsync(new SecurityEvent(SecurityEventNames.LoginLocked, person, [], new Dictionary<string, string>
            {
                ["level"] = outcome.Level.ToString(CultureInfo.InvariantCulture), ["until"] = InstantPattern(outcome.Until!.Value),
            }), Actor.System, ct);
        return outcome.LockedNow;
    }

    public async Task<IResult> SetupAsync(HttpContext http, LocalSetupBody body)
    {
        actor.Current = Actor.System;
        if (!setup.IsOpen)
            return Problem(StatusCodes.Status404NotFound, LocalAccountCodes.SetupClosed, "This instance is already set up.");
        if (Refused(http) is { } refused) return refused;
        var address = TokenAuthentication.ClientAddress(http);
        if (!setup.Matches(body.Token))
        {
            gate.Fail(address);
            return Problem(StatusCodes.Status401Unauthorized, InvalidSetupToken, "This setup address is not the one in the log.");
        }

        var name = body.Name?.Trim() ?? "";
        if (name.Length is 0 or > 200)
            return ResultHttp.Problem(new Failure(PersonCodes.NameRequired, "A name is required.", new Dictionary<string, object?> { ["field"] = "name" }));
        if (!PasswordRules.CheckLogin(body.Login).TryGetValue(out var login))
            return Failed(PasswordRules.CheckLogin(body.Login));
        var password = rules.Check(body.Password, login, name);
        if (!password.TryGetValue(out var normalized))
            return Failed(password);
        var hash = await hasher.HashAsync(normalized!, http.RequestAborted);
        if (hash is null) return Busy();

        var created = await accounts.CreateFirstAdminAsync(name, login!, hash, http.RequestAborted);
        setup.Close();
        if (!created.TryGetValue(out var person))
            return Failed(created);
        await events.RecordAsync(SecurityEvent.Of(SecurityEventNames.SetupCompleted, person!.Id), Actor.System, http.RequestAborted);
        logger.LogInformation("The first administrator of this instance was created on the setup page.");
        return ResultHttp.Json(new LocalLoginView(login!), StatusCodes.Status201Created);
    }

    public async Task<IResult> InspectLinkAsync(HttpContext http, PasswordLinkTokenBody body)
    {
        if (gate.IsBlocked(TokenAuthentication.ClientAddress(http))) return TooMany();
        var link = await accounts.FindLinkAsync(body.Token ?? "", clock.GetCurrentInstant(), http.RequestAborted);
        if (link is null)
        {
            gate.Fail(TokenAuthentication.ClientAddress(http));
            return UnusableLink();
        }
        var login = link.Purpose == CredentialLinkPurposes.Reset
            ? (await accounts.FindAsync(link.Person, http.RequestAborted))?.Login
            : SuggestLogin(link);
        return ResultHttp.Json(new PasswordLinkInfoView(link.Purpose, link.DisplayName, login));
    }

    public async Task<IResult> CompleteLinkAsync(HttpContext http, CompletePasswordLinkBody body)
    {
        // The writes below are the system's, as an OIDC first sign-in's are: no session exists yet.
        actor.Current = Actor.System;
        if (Refused(http) is { } refused) return refused;
        var ct = http.RequestAborted;
        var link = await accounts.FindLinkAsync(body.Token ?? "", clock.GetCurrentInstant(), ct);
        if (link is null)
        {
            gate.Fail(TokenAuthentication.ClientAddress(http));
            return UnusableLink();
        }

        string? login = null;
        if (link.Purpose == CredentialLinkPurposes.Enroll)
        {
            var checkedLogin = PasswordRules.CheckLogin(body.Login);
            if (!checkedLogin.TryGetValue(out login))
                return Failed(checkedLogin);
        }
        var current = login ?? (await accounts.FindAsync(link.Person, ct))?.Login;
        var password = rules.Check(body.Password, current, link.Email, link.DisplayName);
        if (!password.TryGetValue(out var normalized))
            return Failed(password);
        var hash = await hasher.HashAsync(normalized!, ct);
        if (hash is null) return Busy();

        var completed = await accounts.CompleteLinkAsync(body.Token!, login, hash, clock.GetCurrentInstant(), ct);
        if (!completed.TryGetValue(out var done))
            return completed.TryGetFailure(out var failure) && failure is NotFoundFailure ? UnusableLink() : Failed(completed);
        var name = done!.Purpose == CredentialLinkPurposes.Enroll ? SecurityEventNames.AccountEnrolled : SecurityEventNames.PasswordReset;
        await events.RecordAsync(new SecurityEvent(name, done.Person.Id, [], new Dictionary<string, string> { ["link_id"] = done.LinkId.ToString() }),
            Actor.System, ct);
        return ResultHttp.Json(new LocalLoginView(current!));
    }

    /// <summary>
    /// Issue 215, local-accounts.md §8: the adult replaces their own password, the current one asked. A wrong current
    /// password counts towards the lock like a failed sign-in. The other sessions close; this one is reissued.
    /// </summary>
    public async Task<IResult> ChangeMyPasswordAsync(HttpContext http, ChangePasswordBody body)
    {
        if (actor.Current is not { PersonId: { } me } current)
            return Results.Unauthorized();
        if (current.TokenId is not null)
            return ResultHttp.Problem(new Failure(BrowserOnlyCodes.Required, "This action is only available from a browser session.", null));
        var ct = http.RequestAborted;
        var credential = await accounts.FindAsync(me, ct);
        var person = await persons.FindAsync(me, ct);
        if (credential is null || person is null)
            return ResultHttp.Problem(new NotFoundFailure("account", "me"));

        if (await CheckMyPasswordAsync(http, credential, body.Current, "current") is { } wrong)
            return wrong;

        var password = rules.Check(body.New, credential.Login, person.Email, person.DisplayName);
        if (!password.TryGetValue(out var normalized))
            return Failed(password);
        var hash = await hasher.HashAsync(normalized!, ct);
        if (hash is null) return Busy();
        var changed = await invoker.InvokeAsync(new Nid.Core.Application.SetMyPasswordCommand(hash), ct);
        if (!changed.TryGetValue(out var updated))
            return Failed(changed);
        await ReissueAsync(http, updated!);
        return Results.NoContent();
    }

    /// <summary>The session that asked stays open at the adult's new epoch, opened the way it was (password, with a code or not).</summary>
    private Task ReissueAsync(HttpContext http, PersonInfo person)
        => http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            SessionClaims.Create(person, SessionClaims.LocalSubject, CookieAuthenticationDefaults.AuthenticationScheme,
                http.User.FindFirst(SessionClaims.Amr)?.Value ?? "pwd"),
            new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(options.Value.SessionAbsoluteDays) });

    /// <summary>
    /// The adult's own password, asked again before a change to their account. A wrong one counts towards the lock like a
    /// failed sign-in; the answer is 400, not 401: the session is fine, only the password typed is not.
    /// </summary>
    private async Task<IResult?> CheckMyPasswordAsync(HttpContext http, LocalCredential credential, string? typed, string field)
    {
        var ct = http.RequestAborted;
        var now = clock.GetCurrentInstant();
        var locked = credential.LockedUntil is { } until && until > now;
        var check = await hasher.VerifyAsync(credential.PasswordHash, PasswordRules.NormalizePassword(typed ?? ""), ct);
        if (check is null) return Busy();
        if (!locked && check != PasswordCheck.Failed)
            return null;
        if (!locked)
            await CountAccountFailureAsync(credential.Person, now, ct);
        await RecordFailureAsync(credential.Person, locked ? "locked" : "bad_password", TokenAuthentication.ClientAddress(http), ct);
        return ResultHttp.Problem(new Failure(CurrentPasswordWrong, "The current password is wrong, or the account is locked for a few minutes.",
            new Dictionary<string, object?> { ["field"] = field }));
    }

    /// <summary>What a password must not be sent over (spec §12), and an address past its failures (spec §9).</summary>
    private IResult? Refused(HttpContext http)
    {
        if (!http.Request.IsHttps && !settings.AllowInsecureHttp && !IsLoopback(http.Request.Host.Host))
            return Problem(StatusCodes.Status400BadRequest, HttpsRequired,
                "Passwords are only accepted over HTTPS: put Nid behind an HTTPS reverse proxy, or set Auth__AllowInsecureHttp=true on a trusted network.");
        return gate.IsBlocked(TokenAuthentication.ClientAddress(http)) ? TooMany() : null;
    }

    private async Task RecordFailureAsync(PersonId? person, string reason, string address, CancellationToken ct)
    {
        if (failures.Next(person?.Value.ToString() ?? "address:" + address) is not { } count)
            return;
        await events.RecordAsync(new SecurityEvent(SecurityEventNames.LoginFailed, person, [], new Dictionary<string, string>
        {
            ["reason"] = reason, ["count"] = count.ToString(CultureInfo.InvariantCulture), ["address"] = address,
        }), Actor.System, ct);
    }

    /// <summary>The adult's email when there is one, else their name in lower case without spaces (spec §7.1).</summary>
    private static string SuggestLogin(CredentialLinkInfo link)
        => link.Email is { Length: > 0 } email ? email.ToLowerInvariant() : new string(link.DisplayName.ToLowerInvariant().Where(c => !char.IsWhiteSpace(c)).ToArray());

    private static bool IsLoopback(string host)
        => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));

    private static string InstantPattern(Instant at) => NodaTime.Text.InstantPattern.ExtendedIso.Format(at);

    private static IResult Failed<T>(UnambitiousFx.Functional.Result<T> result) where T : notnull
        => ResultHttp.Problem(result.TryGetFailure(out var failure) ? failure! : new Failure("ERROR", "Unknown failure.", null));

    private static IResult UnusableLink()
        => Problem(StatusCodes.Status404NotFound, "auth.link_unusable", "This link does not work any more: ask an administrator for a new one.");

    private static IResult TooMany() => new TooManyResult();

    private static IResult Busy() => new TooManyResult(5);

    private static IResult Problem(int status, string code, string title)
        => Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });

    internal static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class TooManyResult(int retryAfter = 600) : IResult
    {
        public Task ExecuteAsync(HttpContext http)
        {
            http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            http.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
            return Task.CompletedTask;
        }
    }
}
