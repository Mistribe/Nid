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

    /// <param name="demo">Demo mode (issue 203): login leads to the demo page, which signs in as an adult of the demo household.</param>
    public static IEndpointRouteBuilder MapAuth(this IEndpointRouteBuilder routes, bool demo = false)
    {
        routes.MapGet("/auth/login", async (HttpContext http, string? returnUrl, ILoggerFactory loggers) =>
        {
            var target = returnUrl ?? "/";
            if (!ReturnUrl.IsSafe(target))
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "returnUrl must be a relative path.");
            if (demo)
                return Results.Redirect(Demo.DemoEndpoints.PagePath + "?returnUrl=" + Uri.EscapeDataString(target));
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

        routes.MapGet("/auth/session", async (HttpContext http, IPersonRegistry persons, IOptions<NidAuthOptions> options) =>
        {
            var personId = SessionClaims.ReadPersonId(http.User);
            var person = personId is { } id ? await persons.FindAsync(id, http.RequestAborted) : null;
            if (person is null)
                return ResultHttp.Json(new SessionResponse(false, null, null, null, null, demo ? null : options.Value.Authority, demo));

            var preferences = await persons.PreferencesAsync(person.Id, http.RequestAborted);

            return ResultHttp.Json(new SessionResponse(
                true,
                new SessionPerson(person.Id.Value, person.DisplayName, person.Kind.ToString().ToLowerInvariant()),
                SessionClaims.CsrfToken(options.Value.CsrfKey, person.Id, person.SessionEpoch),
                person.SessionEpoch,
                preferences,
                demo ? null : options.Value.Authority,
                demo));
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
/// demo mode (issue 203), the app shows it on every screen.
/// </summary>
public sealed record SessionResponse(
    bool Authenticated, SessionPerson? Person, string? Csrf, int? SessionEpoch, Preferences? Preferences, string? IdentityProvider, bool Demo = false);
