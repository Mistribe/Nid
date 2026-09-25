using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Nid.Subscriptions.Contracts;

namespace Nid.Subscriptions.Domain.Catalog;

/// <summary>The rules every provider and label obeys, wherever it comes from: the catalog, the API, an import (design §3, §4).</summary>
public static partial class ProviderRules
{
    // \z, never $: in .NET, $ also matches before a final newline, and "netflix\n" is not a key (Review Focus 2).
    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,63}\z")]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"^#[0-9A-Fa-f]{6}\z")]
    private static partial Regex RgbPattern();

    [GeneratedRegex(@"^#[0-9A-Fa-f]{8}\z")]
    private static partial Regex ArgbPattern();

    public static bool IsKey([NotNullWhen(true)] string? key) => key is not null && KeyPattern().IsMatch(key);

    /// <summary>The name trimmed, or <c>null</c> when it is empty or longer than <paramref name="maxLength"/>.</summary>
    public static string? Name(string? name, int maxLength)
    {
        var trimmed = name?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > maxLength ? null : trimmed;
    }

    /// <summary>Absent, or an absolute <c>http</c>/<c>https</c> address of at most 2 048 characters.</summary>
    public static bool IsUrl(string? url)
        => url is null
           || (url.Length <= CatalogLimits.MaxUrlLength
               && Uri.TryCreate(url, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps));

    /// <summary>A URL a person typed: blank means absent. What is left still has to pass <see cref="IsUrl"/>.</summary>
    public static string? Url(string? url) => string.IsNullOrWhiteSpace(url) ? null : url.Trim();

    /// <summary>
    /// 1C2c design §5: an icon is an absolute https URL of at most 2 048 characters, without a user name. Answers the
    /// reason it is not (<see cref="IconCodes.NotHttps"/> for another scheme, <see cref="IconCodes.InvalidUrl"/>
    /// otherwise), or <c>null</c>.
    /// </summary>
    public static string? IconProblem(string url)
    {
        if (url.Length > CatalogLimits.MaxUrlLength || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0)
            return IconCodes.InvalidUrl;
        return uri.Scheme == Uri.UriSchemeHttps ? null : IconCodes.NotHttps;
    }

    /// <summary>CSS <c>#RRGGBB</c> as is; <c>#AARRGGBB</c> without its alpha (the schema forces <c>FF</c>, design §8); anything else <c>null</c>.</summary>
    public static string? Color(string? color)
        => color is null ? null
            : RgbPattern().IsMatch(color) ? color
            : ArgbPattern().IsMatch(color) ? "#" + color[3..]
            : null;

    /// <summary><c>home</c> → "Home", <c>insurance-home</c> → "Insurance home": the migration's rule for adopted names too.</summary>
    public static string NameFromKey(string key, int maxLength)
    {
        var spaced = key.Replace('-', ' ').Trim();
        var name = spaced.Length == 0 ? key : char.ToUpperInvariant(spaced[0]) + spaced[1..];
        return name.Length > maxLength ? name[..maxLength] : name;
    }

    public const int SuffixLength = 6;

    private const int MaxSlugLength = CatalogLimits.MaxKeyLength - 1 - SuffixLength; // 57: "<slug>-<6 chars>" stays within 64

    /// <summary>
    /// Design §3.3: the name with accents folded, lowercase, every run of anything else as one hyphen, cut at 57
    /// characters and never ending on a hyphen; "provider" when nothing is left (Review Focus 4).
    /// </summary>
    public static string Slug(string name)
    {
        var slug = new StringBuilder(name.Length);
        foreach (var c in name.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            var lower = char.ToLowerInvariant(c);
            if (lower is >= 'a' and <= 'z' or >= '0' and <= '9')
                slug.Append(lower);
            else if (slug.Length > 0 && slug[^1] != '-')
                slug.Append('-');
        }

        var text = slug.ToString().TrimEnd('-');
        if (text.Length > MaxSlugLength)
            text = text[..MaxSlugLength].TrimEnd('-');
        return text.Length == 0 ? "provider" : text;
    }
}
