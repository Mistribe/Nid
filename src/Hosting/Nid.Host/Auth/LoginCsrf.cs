using System.Security.Cryptography;
using System.Text;

namespace Nid.Host.Auth;

/// <summary>
/// local-accounts.md §11: the routes that run before a session exists (password sign-in, setup, password links) are
/// protected against login CSRF by a pre-session cookie (<c>__Host-nid-pre</c>, <c>SameSite=Strict</c>, 30 minutes), a token
/// derived from it that <c>/auth/session</c> hands out, a required same-origin <c>Origin</c>, and a JSON body.
/// </summary>
public static class LoginCsrf
{
    public static readonly string[] Paths = ["/auth/login/password", "/auth/login/totp", "/auth/setup", "/auth/link/inspect", "/auth/link/complete"];

    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    public static string CookieName(AuthSettings settings) => settings.AllowInsecureHttp ? "nid-pre" : "__Host-nid-pre";

    public static bool Protects(PathString path) => Paths.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>The pre-session cookie's value, set on the response when the browser has none yet.</summary>
    public static string Ensure(HttpContext http, AuthSettings settings)
    {
        var name = CookieName(settings);
        if (http.Request.Cookies.TryGetValue(name, out var existing) && existing is { Length: 43 })
            return existing;
        var value = Base64Url(RandomNumberGenerator.GetBytes(32));
        http.Response.Cookies.Append(name, value, Options(http, settings));
        return value;
    }

    /// <summary>Removed once a session opens: a new sign-in starts from a new cookie.</summary>
    public static void Clear(HttpContext http, AuthSettings settings)
        => http.Response.Cookies.Delete(CookieName(settings), Options(http, settings));

    public static string Token(string key, string cookie)
        => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes("pre:" + cookie)));

    /// <summary>Whether a request to one of <see cref="Paths"/> passes the four checks of §11.</summary>
    public static bool IsValid(HttpRequest request, AuthSettings settings, string publicUrl, string key)
    {
        if (!request.Headers.TryGetValue("Origin", out var origin)
            || !string.Equals(origin.ToString().TrimEnd('/'), publicUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            return false;
        if (request.Headers.TryGetValue("Sec-Fetch-Site", out var site) && site != "same-origin")
            return false;
        if (request.ContentType is not { } type || !type.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!request.Cookies.TryGetValue(CookieName(settings), out var cookie) || string.IsNullOrEmpty(cookie)
            || !request.Headers.TryGetValue(CsrfMiddleware.HeaderName, out var supplied))
            return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied.ToString()), Encoding.UTF8.GetBytes(Token(key, cookie)));
    }

    private static CookieOptions Options(HttpContext http, AuthSettings settings) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Secure = !settings.AllowInsecureHttp || http.Request.IsHttps,
        Path = "/",
        MaxAge = Lifetime,
        IsEssential = true,
    };

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
