using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Nid.Core.Contracts;

namespace Nid.Host.Auth;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuth(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/auth/login", (string? returnUrl) =>
            ReturnUrl.IsSafe(returnUrl ?? "/")
                ? Results.Challenge(new AuthenticationProperties { RedirectUri = returnUrl ?? "/" }, [OpenIdConnectDefaults.AuthenticationScheme])
                : Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "returnUrl must be a relative path."))
            .ExcludeFromDescription();

        routes.MapPost("/auth/logout", async (HttpContext http) =>
        {
            // Only Nid's cookie is removed: Authelia has no standard logout, its own session stays open.
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        })
            .Produces(StatusCodes.Status204NoContent);

        routes.MapGet("/auth/session", async (HttpContext http, IPersonRegistry persons, IOptions<NidAuthOptions> options) =>
        {
            var personId = SessionClaims.ReadPersonId(http.User);
            var person = personId is { } id ? await persons.FindAsync(id, http.RequestAborted) : null;
            if (person is null)
                return Results.Json(new SessionResponse(false, null, null, null, null, options.Value.Authority));

            var preferences = await persons.PreferencesAsync(person.Id, http.RequestAborted);

            return Results.Json(new SessionResponse(
                true,
                new SessionPerson(person.Id.Value, person.DisplayName, person.Kind.ToString().ToLowerInvariant()),
                SessionClaims.CsrfToken(options.Value.CsrfKey, person.Id, person.SessionEpoch),
                person.SessionEpoch,
                preferences,
                options.Value.Authority));
        })
            .Produces<SessionResponse>();

        return routes;
    }
}

public sealed record SessionPerson(Guid Id, string Name, string Kind);

/// <summary>
/// What the SPA reads first (frontend/architecture.md §3). Signed out: only <c>Authenticated</c> and
/// <c>IdentityProvider</c> are set. <c>IdentityProvider</c> is the OIDC authority, whose own session outlives
/// a Nid sign-out: the signed-out page links to it (identity-and-access/authentication.md).
/// </summary>
public sealed record SessionResponse(bool Authenticated, SessionPerson? Person, string? Csrf, int? SessionEpoch, Preferences? Preferences, string? IdentityProvider);
