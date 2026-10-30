using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Nid.Banking.Domain;

/// <summary>Spec §6.2: a bank label as rules and fingerprints read it, upper-case, accents and bank prefixes gone.</summary>
public static partial class TransactionLabel
{
    /// <summary>Removed when they open the label, longest first.</summary>
    internal static readonly string[] Prefixes =
    [
        "PRLV SEPA ", "PRELEVEMENT EUROPEEN ", "PRELEVEMENT ", "VIR SEPA ", "VIR INST ", "VIREMENT ", "VIR ",
        "PAIEMENT PAR CARTE ", "PAIEMENT CB ", "CB ",
    ];

    // « CARTE X1234 », a leading date (12/10, 12/10/26, 12/10/2026) and a leading card number.
    [GeneratedRegex(@"^(CARTE X\d{4} |\d{2}/\d{2}(/\d{2,4})? |X\d{4} )")]
    private static partial Regex LeadingPattern();

    [GeneratedRegex(@"\bX\d{4}\b")]
    private static partial Regex CardNumber();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    public static string Normalize(string label)
    {
        var decomposed = label.ToUpperInvariant().Normalize(NormalizationForm.FormD);
        var text = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                text.Append(c);

        var result = Spaces().Replace(text.ToString().Normalize(NormalizationForm.FormC), " ").TrimStart();
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (var prefix in Prefixes)
                if (result.StartsWith(prefix, StringComparison.Ordinal))
                {
                    result = result[prefix.Length..];
                    changed = true;
                }

            if (LeadingPattern().Match(result) is { Success: true } m)
            {
                result = result[m.Length..];
                changed = true;
            }
        }

        return Spaces().Replace(CardNumber().Replace(result, " "), " ").Trim();
    }

    /// <summary>What a recurring debit keeps from one month to the next: the normalized label without its numbers, references and dates.</summary>
    public static string Stem(string normalizedLabel)
        => string.Join(' ', normalizedLabel.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => !t.Any(char.IsDigit)));
}
