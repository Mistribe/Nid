using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Nid.Core.Contracts;

namespace Nid.Host.Auth;

public static class SessionClaims
{
    public const string PersonId = "nid:person_id";
    public const string Subject = "nid:sub";
    public const string Epoch = "nid:session_epoch";

    public static ClaimsPrincipal Create(PersonInfo person, string subject, string scheme)
        => new(new ClaimsIdentity(
        [
            new Claim(PersonId, person.Id.Value.ToString()),
            new Claim(Subject, subject),
            new Claim(Epoch, person.SessionEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ], scheme));

    public static PersonId? ReadPersonId(ClaimsPrincipal user)
        => Guid.TryParse(user.FindFirstValue(PersonId), out var id) ? new PersonId(id) : null;

    public static int? ReadEpoch(ClaimsPrincipal user)
        => int.TryParse(user.FindFirstValue(Epoch), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var epoch) ? epoch : null;

    /// <summary>CSRF token bound to the person and the session epoch: "sign out everywhere" invalidates it too.</summary>
    public static string CsrfToken(string key, PersonId person, int epoch)
    {
        var message = Encoding.UTF8.GetBytes($"{person.Value:N}:{epoch}");
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), message));
    }
}

public static class ReturnUrl
{
    /// <summary>Only a relative path of this application is accepted: anything else is an open redirect.</summary>
    public static bool IsSafe(string? url)
        => !string.IsNullOrEmpty(url)
           && url[0] == '/'
           && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'))
           && !url.Contains('\n') && !url.Contains('\r');
}
