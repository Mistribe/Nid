using System.Globalization;

namespace Nid.Documents.Domain;

public static class FileNames
{
    private const int MaxLength = 255;
    private const string Fallback = "document";

    /// <summary>Last path segment, without control, format (bidi override) or header-breaking characters, at most 255 chars.</summary>
    public static string Clean(string? raw)
    {
        var name = raw ?? "";
        name = name[(name.LastIndexOfAny(['/', '\\']) + 1)..];
        name = Strip(name).Trim();
        if (name.Length == 0 || name.All(c => c == '.')) return Fallback;
        if (name.Length <= MaxLength) return name;

        var ext = Path.GetExtension(name);
        if (ext.Length > 32) ext = "";
        var n = MaxLength - ext.Length;
        if (char.IsHighSurrogate(name[n - 1])) n--; // never cut a surrogate pair in half
        return name[..n] + ext;
    }

    // Rune enumeration by hand: TryGetRuneAt fails on a lone surrogate, which is dropped.
    private static string Strip(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        for (var i = 0; i < name.Length; i++)
        {
            if (!System.Text.Rune.TryGetRuneAt(name, i, out var r)) continue;
            if (r.Utf16SequenceLength == 2) i++;
            if (r.Value is '"' or '\\' || System.Text.Rune.GetUnicodeCategory(r) is UnicodeCategory.Control
                or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) continue;
            sb.Append(r.ToString());
        }
        return sb.ToString();
    }

    public static string TitleOf(string cleaned)
    {
        var title = Path.GetFileNameWithoutExtension(cleaned);
        return title.Length == 0 ? cleaned : title;
    }
}
