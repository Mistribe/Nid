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
                : Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "returnUrl must be a relative path."));

        routes.MapPost("/auth/logout", async (HttpContext http) =>
        {
            // Only Nid's cookie is removed: Authelia has no standard logout, its own session stays open.
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });

        routes.MapGet("/auth/session", async (HttpContext http, IPersonRegistry persons, IOptions<NidAuthOptions> options) =>
        {
            var personId = SessionClaims.ReadPersonId(http.User);
            var person = personId is { } id ? await persons.FindAsync(id, http.RequestAborted) : null;
            if (person is null)
                return Results.Json(new { authenticated = false });

            return Results.Json(new
            {
                authenticated = true,
                person = new { id = person.Id.Value, name = person.DisplayName, kind = person.Kind.ToString().ToLowerInvariant() },
                csrf = SessionClaims.CsrfToken(options.Value.CsrfKey, person.Id, person.SessionEpoch),
                session_epoch = person.SessionEpoch,
            });
        });

        return routes;
    }
}
