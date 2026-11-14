using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Nid.Core.Contracts;
using Nid.Host.Api;
using Nid.Platform.Contracts;

namespace Nid.Host.Auth;

public static class AuthEndpoints
{
    /// <summary>Where <c>/auth/login</c> sends the browser when the identity provider does not answer.</summary>
    public const string LoginUnavailablePath = "/login-unavailable";

    /// <summary>The web app's sign-in page, where <c>/auth/login</c> sends the browser when local accounts are on (issue 215).</summary>
    public const string LoginPagePath = "/login";

    private static AuthSettings settings(IEndpointRouteBuilder routes) => routes.ServiceProvider.GetRequiredService<AuthSettings>();

    private static async Task<IResult> ChallengeOidcAsync(HttpContext http, string target, ILoggerFactory loggers)
    {
        try
        {
            await http.ChallengeAsync(OpenIdConnectDefaults.AuthenticationScheme, new AuthenticationProperties { RedirectUri = target });
            return Results.Empty;
        }
        catch (InvalidOperationException e) when (!http.Response.HasStarted)
        {
            // The identity provider's discovery document could not be read (it or its proxy is down): an empty 500
            // left a blank page, the web page says what is wrong and offers to try again (issue 363).
            loggers.CreateLogger(typeof(AuthEndpoints)).LogError(e, "The identity provider could not be reached to sign in.");
            return Results.Redirect(LoginUnavailablePath + "?returnUrl=" + Uri.EscapeDataString(target));
        }
    }

    /// <param name="demo">Demo mode (issue 203): login leads to the demo page, which signs in as an adult of the demo household.</param>
    public static IEndpointRouteBuilder MapAuth(this IEndpointRouteBuilder routes, bool demo = false)
    {
        routes.MapGet("/auth/login", async (HttpContext http, string? returnUrl, AuthSettings settings, ILoggerFactory loggers) =>
        {
            var target = returnUrl ?? "/";
            if (!ReturnUrl.IsSafe(target))
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "returnUrl must be a relative path.");
            if (demo)
                return Results.Redirect(Demo.DemoEndpoints.PagePath + "?returnUrl=" + Uri.EscapeDataString(target));
            // Issue 215: with local accounts the web app's sign-in page asks for the password (and offers the identity
            // provider in both modes); OIDC alone goes straight to the identity provider, as before.
            if (settings.Local)
                return Results.Redirect(LoginPagePath + "?returnUrl=" + Uri.EscapeDataString(target));
            return await ChallengeOidcAsync(http, target, loggers);
        })
            .ExcludeFromDescription();

        if (!demo && settings(routes).Mode == AuthMode.Both)
            routes.MapGet("/auth/login/oidc", async (HttpContext http, string? returnUrl, ILoggerFactory loggers) =>
            {
                var target = returnUrl ?? "/";
                if (!ReturnUrl.IsSafe(target))
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "returnUrl must be a relative path.");
                return await ChallengeOidcAsync(http, target, loggers);
            })
                .ExcludeFromDescription();

        routes.MapPost("/auth/logout", async (HttpContext http, ISecurityEvents events) =>
        {
            if (SessionClaims.ReadPersonId(http.User) is { } person)
                await events.RecordAsync(SecurityEvent.Of(SecurityEventNames.Logout, person), Actor.Browser(person), http.RequestAborted);
            // Only Nid's cookie is removed: the identity provider's own session stays open (authentication.md §6).
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        })
            .Produces(StatusCodes.Status204NoContent);

        routes.MapGet("/auth/session", async (HttpContext http, IPersonRegistry persons, IOptions<NidAuthOptions> options, AuthSettings settings) =>
        {
            var personId = SessionClaims.ReadPersonId(http.User);
            var person = personId is { } id ? await persons.FindAsync(id, http.RequestAborted) : null;
            // The identity provider's address only where it is used: the signed-out page links to its portal.
            var identityProvider = demo || !settings.Oidc ? null : options.Value.Authority;
            IReadOnlyList<string> methods = demo ? [] : settings.Methods;
            if (person is null)
            {
                // Issue 215: a fresh instance with local accounts waits for its first administrator (local-accounts spec §5).
                var setupRequired = !demo && settings.Local && (await persons.AdultIdsAsync(http.RequestAborted)).Count == 0;
                // The password form, the setup and the links post before any session: their CSRF token comes from a
                // pre-session cookie set here (local-accounts spec §11).
                var preCsrf = !demo && settings.Local ? LoginCsrf.Token(options.Value.CsrfKey, LoginCsrf.Ensure(http, settings)) : null;
                return ResultHttp.Json(new SessionResponse(false, null, preCsrf, null, null, identityProvider, demo, methods, setupRequired));
            }

            var preferences = await persons.PreferencesAsync(person.Id, http.RequestAborted);

            return ResultHttp.Json(new SessionResponse(
                true,
                new SessionPerson(person.Id.Value, person.DisplayName, person.Kind.ToString().ToLowerInvariant()),
                SessionClaims.CsrfToken(options.Value.CsrfKey, person.Id, person.SessionEpoch),
                person.SessionEpoch,
                preferences,
                identityProvider,
                demo,
                methods,
                false));
        })
            .Produces<SessionResponse>();

        return routes;
    }
}

public sealed record SessionPerson(Guid Id, string Name, string Kind);

/// <summary>
/// What the SPA reads first (frontend/architecture.md §3). Signed out: only <c>Authenticated</c> and
/// <c>IdentityProvider</c> are set. <c>IdentityProvider</c> is the OIDC authority, whose own session outlives
/// a Nid sign-out: the signed-out page links to it (identity-and-access/authentication.md). <c>Demo</c>: the instance runs in
/// demo mode (issue 203), the app shows it on every screen. <c>Methods</c> (issue 215): how one signs in, <c>password</c>
/// and/or <c>oidc</c>, empty in demo mode. <c>SetupRequired</c>: local accounts are on and no adult exists yet, the web app
/// says where to find the setup address (local-accounts spec §5).
/// </summary>
public sealed record SessionResponse(
    bool Authenticated, SessionPerson? Person, string? Csrf, int? SessionEpoch, Preferences? Preferences, string? IdentityProvider,
    bool Demo, IReadOnlyList<string> Methods, bool SetupRequired);
