using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Nid.Host.Auth;

/// <summary>
/// Unsafe methods need a custom header carrying the session's CSRF token, and a same-origin
/// <c>Origin</c> / <c>Sec-Fetch-Site</c> when the browser sends them. No CORS is configured (single origin).
/// </summary>
public sealed class CsrfMiddleware(RequestDelegate next, IOptions<NidAuthOptions> options, AuthSettings settings)
{
    public const string HeaderName = "X-Nid-Csrf";

    private static readonly HashSet<string> SafeMethods = ["GET", "HEAD", "OPTIONS"];

    public async Task InvokeAsync(HttpContext context)
    {
        // Issue 215: before a session exists, the pre-session cookie and its token stand in for the session's (LoginCsrf).
        if (!SafeMethods.Contains(context.Request.Method) && LoginCsrf.Protects(context.Request.Path))
        {
            if (!LoginCsrf.IsValid(context.Request, settings, options.Value.PublicUrl, options.Value.CsrfKey))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            await next(context);
            return;
        }

        // 4A: a bearer request carries no ambient credential: a cross-site page cannot forge one (spec §3), and the
        // cookie never authenticates it (the "nid" policy scheme), so a bad token answers 401, not this 403.
        // 4B: only on the API; /auth/logout always keeps its check (a bearer header must not spare a cookie POST).
        if (SafeMethods.Contains(context.Request.Method) || !RequiresCsrf(context.Request.Path)
            || (TokenAuthentication.IsBearer(context.Request) && context.Request.Path.StartsWithSegments("/api")))
        {
            await next(context);
            return;
        }

        if (!IsSameOrigin(context.Request) || !HasValidToken(context))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await next(context);
    }

    private static bool RequiresCsrf(PathString path)
        => (path.StartsWithSegments("/api") && !path.StartsWithSegments(Api.DocumentEndpoints.UploadsPath)) || path.StartsWithSegments("/auth/logout");

    private bool IsSameOrigin(HttpRequest request)
    {
        if (request.Headers.TryGetValue("Sec-Fetch-Site", out var site) && site != "same-origin" && site != "none")
            return false;

        if (request.Headers.TryGetValue("Origin", out var origin)
            && !string.Equals(origin.ToString().TrimEnd('/'), options.Value.PublicUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private bool HasValidToken(HttpContext context)
    {
        var person = SessionClaims.ReadPersonId(context.User);
        var epoch = SessionClaims.ReadEpoch(context.User);
        if (person is null || epoch is null || !context.Request.Headers.TryGetValue(HeaderName, out var supplied))
            return false;

        var expected = SessionClaims.CsrfToken(options.Value.CsrfKey, person.Value, epoch.Value);
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied.ToString()), Encoding.UTF8.GetBytes(expected));
    }
}
