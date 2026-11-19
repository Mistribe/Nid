using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Host.Auth;

/// <summary>
/// Issue 215, local-accounts.md §10: time-based one-time codes (RFC 6238) as every common authenticator app computes them:
/// HMAC-SHA-1, 6 digits, 30-second steps, one step of drift either way.
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public const int PeriodSeconds = 30;
    public const string Issuer = "Nid";

    /// <summary>160 bits, the size RFC 4226 recommends for HMAC-SHA-1.</summary>
    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    public static long StepAt(Instant now) => now.ToUnixTimeSeconds() / PeriodSeconds;

    public static string Code(byte[] secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);
        var hash = HMACSHA1.HashData(secret, counter);
        var offset = hash[^1] & 0x0f;
        var value = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (value % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>The step whose code <paramref name="code"/> is, within one step of <paramref name="now"/>; <c>null</c> otherwise.</summary>
    public static long? Match(byte[] secret, string? code, Instant now)
    {
        var digits = new string((code ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (digits.Length != Digits || !digits.All(char.IsAsciiDigit))
            return null;
        var current = StepAt(now);
        long? found = null;
        // Every candidate is compared, in constant time: the answer does not tell which step was close.
        for (var step = current - 1; step <= current + 1; step++)
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, step)), Encoding.ASCII.GetBytes(digits)))
                found = step;
        return found;
    }

    /// <summary>RFC 4648 base32 without padding, what the apps take when the secret is typed by hand.</summary>
    public static string Base32(byte[] bytes)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new StringBuilder((bytes.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                output.Append(alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) output.Append(alphabet[(buffer << (5 - bits)) & 31]);
        return output.ToString();
    }

    /// <summary>The <c>otpauth://</c> address the QR code carries (Key Uri Format).</summary>
    public static string Uri(byte[] secret, string account)
        => $"otpauth://totp/{System.Uri.EscapeDataString(Issuer)}:{System.Uri.EscapeDataString(account)}"
           + $"?secret={Base32(secret)}&issuer={System.Uri.EscapeDataString(Issuer)}&algorithm=SHA1&digits={Digits}&period={PeriodSeconds}";
}

/// <summary>
/// local-accounts.md §10: ten recovery codes of 80 bits, written <c>xxxx-xxxx-xxxx-xxxx</c> in Crockford's base32 (no I, L,
/// O or U), kept by the SHA-256 of their normalised form.
/// </summary>
public static class RecoveryCodes
{
    public const int Count = 10;
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static IReadOnlyList<string> New()
        => Enumerable.Range(0, Count).Select(_ =>
        {
            var bytes = RandomNumberGenerator.GetBytes(10);
            var chars = new char[16];
            // 80 bits, 16 characters of 5 bits.
            var value = new System.Numerics.BigInteger(bytes, isUnsigned: true);
            for (var i = 15; i >= 0; i--)
            {
                chars[i] = Alphabet[(int)(value & 31)];
                value >>= 5;
            }
            var text = new string(chars);
            return $"{text[..4]}-{text[4..8]}-{text[8..12]}-{text[12..]}";
        }).ToList();

    /// <summary>Upper case without dashes or spaces, the look-alike letters read as digits; <c>null</c> when it cannot be a code.</summary>
    public static string? Normalize(string? typed)
    {
        if (typed is null) return null;
        var chars = typed.Where(c => c != '-' && !char.IsWhiteSpace(c)).Select(c => char.ToUpperInvariant(c) switch
        {
            'O' => '0',
            'I' or 'L' => '1',
            var other => other,
        }).ToArray();
        return chars.Length == 16 && chars.All(c => Alphabet.Contains(c)) ? new string(chars) : null;
    }

    public static string Hash(string normalized) => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(normalized)));
}

/// <summary>The TOTP secret at rest, encrypted by Data Protection (purpose <c>Nid.LocalAccounts.Totp</c>): it has to be read back.</summary>
public sealed class TotpSecrets(IDataProtectionProvider provider)
{
    private readonly IDataProtector protector = provider.CreateProtector("Nid.LocalAccounts.Totp");

    public string Protect(byte[] secret) => protector.Protect(Convert.ToBase64String(secret));

    public byte[]? Unprotect(string? stored)
    {
        if (stored is null) return null;
        try
        {
            return Convert.FromBase64String(protector.Unprotect(stored));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}

/// <summary>The password step passed: whose, at which session epoch, where to go next, and this step's own id.</summary>
public sealed record MfaTicket(PersonId Person, int Epoch, string ReturnUrl, string Nonce);

/// <summary>
/// local-accounts.md §10: between a right password and the code, the cookie <c>__Host-nid-mfa</c> (Data Protection, 5
/// minutes) says who is signing in; it opens no session. Three wrong codes on one step send back to the password.
/// </summary>
public sealed class MfaStep(IDataProtectionProvider provider, AuthSettings settings, IClock clock)
{
    public const int MaxAttempts = 3;
    private static readonly NodaTime.Duration Lifetime = NodaTime.Duration.FromMinutes(5);
    private const int MaxRemembered = 10_000;
    private readonly IDataProtector protector = provider.CreateProtector("Nid.LocalAccounts.MfaStep");
    private readonly ConcurrentDictionary<string, int> attempts = new();

    public string CookieName => settings.AllowInsecureHttp ? "nid-mfa" : "__Host-nid-mfa";

    public void Issue(HttpContext http, PersonInfo person, string returnUrl)
    {
        var expires = clock.GetCurrentInstant().Plus(Lifetime).ToUnixTimeSeconds();
        var nonce = LocalSignIn.Base64Url(RandomNumberGenerator.GetBytes(16));
        var payload = string.Join('\n', person.Id.Value.ToString(), person.SessionEpoch.ToString(CultureInfo.InvariantCulture),
            expires.ToString(CultureInfo.InvariantCulture), nonce, returnUrl);
        http.Response.Cookies.Append(CookieName, protector.Protect(payload), Options(http));
    }

    public MfaTicket? Read(HttpContext http)
    {
        if (!http.Request.Cookies.TryGetValue(CookieName, out var value) || string.IsNullOrEmpty(value))
            return null;
        string payload;
        try
        {
            payload = protector.Unprotect(value);
        }
        catch (CryptographicException)
        {
            return null;
        }
        var parts = payload.Split('\n');
        if (parts.Length != 5
            || !Guid.TryParse(parts[0], out var person)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var epoch)
            || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var expires)
            || clock.GetCurrentInstant().ToUnixTimeSeconds() >= expires
            || attempts.GetValueOrDefault(parts[3]) >= MaxAttempts)
            return null;
        return new MfaTicket(new PersonId(person), epoch, parts[4], parts[3]);
    }

    /// <summary>A wrong code on this step: <c>true</c> when it was the last one allowed.</summary>
    public bool Fail(MfaTicket ticket)
    {
        if (attempts.Count > MaxRemembered) attempts.Clear();
        return attempts.AddOrUpdate(ticket.Nonce, 1, (_, n) => n + 1) >= MaxAttempts;
    }

    public void Clear(HttpContext http, MfaTicket? ticket)
    {
        if (ticket is not null) attempts.TryRemove(ticket.Nonce, out _);
        http.Response.Cookies.Delete(CookieName, Options(http));
    }

    private CookieOptions Options(HttpContext http) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Secure = !settings.AllowInsecureHttp || http.Request.IsHttps,
        Path = "/",
        MaxAge = Lifetime.ToTimeSpan(),
        IsEssential = true,
    };
}
