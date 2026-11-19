using System.Globalization;
using System.Text;
using Nid.Core.Contracts;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Host.Auth;

/// <summary>
/// local-accounts.md §4 (NIST SP 800-63B): a minimum length, any character, no composition rule, a local list of
/// common passwords; nothing leaves the server. And §7: what an identifier may be.
/// </summary>
public sealed class PasswordRules
{
    public const int MaxLength = 256;
    public const int LoginMaxLength = 254;

    private static readonly Lazy<HashSet<string>> Common = new(LoadCommon);

    private static readonly string[] Rows =
        ["1234567890", "qwertyuiop", "azertyuiop", "qwertzuiop", "asdfghjkl", "qsdfghjklm", "zxcvbnm", "wxcvbn", "abcdefghijklmnopqrstuvwxyz"];

    private readonly int minLength;

    public PasswordRules(LocalAccountOptions options) => minLength = options.Bounded().MinPasswordLength;

    public int MinLength => minLength;

    /// <summary>NFKC, so a password typed on two keyboards hashes the same; never trimmed.</summary>
    public static string NormalizePassword(string password) => password.Normalize(NormalizationForm.FormKC);

    /// <summary>Trimmed, NFKC, lower case: the stored form of an identifier, and how one is looked up.</summary>
    public static string NormalizeLogin(string? login) => (login ?? "").Trim().Normalize(NormalizationForm.FormKC).ToLowerInvariant();

    public static Result<string> CheckLogin(string? login)
    {
        var normalized = NormalizeLogin(login);
        if (normalized.Length is 0 or > LoginMaxLength || normalized.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            return Result.Failure<string>(Field(LocalAccountCodes.LoginInvalid, "An identifier is 1 to 254 characters, without spaces.", "login"));
        return Result.Success(normalized);
    }

    /// <summary>
    /// The password, normalised, or why it is refused. <paramref name="context"/>: the identifier, the email and the name of
    /// the adult, which the password must not contain.
    /// </summary>
    public Result<string> Check(string? password, params string?[] context)
    {
        var normalized = NormalizePassword(password ?? "");
        var length = new StringInfo(normalized).LengthInTextElements;
        if (length < minLength)
            return Result.Failure<string>(Field(LocalAccountCodes.PasswordTooShort, $"A password is at least {minLength} characters.", "password"));
        if (length > MaxLength)
            return Result.Failure<string>(Field(LocalAccountCodes.PasswordTooLong, $"A password is at most {MaxLength} characters.", "password"));
        if (IsCommon(normalized, context))
            return Result.Failure<string>(Field(LocalAccountCodes.PasswordTooCommon, "This password is too common: choose another.", "password"));
        return Result.Success(normalized);
    }

    private static bool IsCommon(string password, string?[] context)
    {
        var lower = password.ToLowerInvariant();
        if (Common.Value.Contains(lower))
            return true;
        // Very few distinct characters: « aaaaaaaaaaaa », « abababababab ».
        if (lower.Distinct().Count() <= 2)
            return true;
        // A run along a keyboard row or the alphabet, forwards or backwards, repeated or followed by digits.
        var letters = new string(lower.Where(char.IsLetterOrDigit).ToArray());
        if (letters.Length >= lower.Length - 1 && Rows.Any(row => IsAlong(letters, row)))
            return true;
        // « nid » followed by digits and symbols only.
        if (lower.StartsWith("nid", StringComparison.Ordinal) && lower[3..].All(c => !char.IsLetter(c)))
            return true;
        foreach (var word in context.SelectMany(Words))
            if (lower.Contains(word, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>The identifier, the email's local part and the name's words of 4 letters or more.</summary>
    private static IEnumerable<string> Words(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) yield break;
        var lower = value.Trim().ToLowerInvariant();
        var at = lower.IndexOf('@');
        if (at > 0) lower = lower[..at];
        if (lower.Length >= 4) yield return lower;
        foreach (var part in lower.Split([' ', '.', '-', '_'], StringSplitOptions.RemoveEmptyEntries))
            if (part.Length >= 4 && part != lower)
                yield return part;
    }

    /// <summary>Whether the text is a run of the row (or its reverse), repeated, possibly ended by digits.</summary>
    private static bool IsAlong(string text, string row)
    {
        var core = text.TrimEnd("0123456789".ToCharArray());
        if (core.Length < 4) core = text;
        foreach (var line in new[] { row, new string(row.Reverse().ToArray()) })
        {
            var start = line.IndexOf(core[0]);
            if (start < 0) continue;
            var position = start;
            var along = true;
            foreach (var c in core)
            {
                if (line[position] != c) { along = false; break; }
                position = (position + 1) % line.Length;
            }
            if (along) return true;
        }
        return false;
    }

    private static HashSet<string> LoadCommon()
    {
        using var stream = typeof(PasswordRules).Assembly.GetManifestResourceStream("Nid.Host.Auth.common-passwords.txt")
            ?? throw new InvalidOperationException("The list of common passwords is missing from the build.");
        using var reader = new StreamReader(stream);
        var set = new HashSet<string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
            if (line.Length > 0 && line[0] != '#')
                set.Add(line.ToLowerInvariant());
        return set;
    }

    private static Failure Field(string code, string message, string field)
        => new(code, message, new Dictionary<string, object?> { ["field"] = field });
}
