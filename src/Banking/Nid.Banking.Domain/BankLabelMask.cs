using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Nid.Banking.Domain;

/// <summary>
/// #450 (<c>docs/ai/README.md</c> §6.5): what is left of a bank label, a counterparty or a category name before it leaves Banking for a
/// model. Emails, IBANs, phone numbers, any run of 5 digits or more (a card or account number, spaces inside it included) and the words of
/// the household's names (<see cref="CategorySuggestions.NameWords"/>, persons and dependants) become placeholders.
/// </summary>
public static partial class BankLabelMask
{
    public const string Name = "[NOM]", Iban = "[IBAN]", Email = "[EMAIL]", Phone = "[TEL]", Number = "[N]";

    public static string Mask(string text, IReadOnlyCollection<string> nameWords)
    {
        var masked = EmailShape().Replace(text, Email);
        masked = IbanShape().Replace(masked, Iban);
        masked = FrenchPhone().Replace(masked, Phone);
        masked = InternationalPhone().Replace(masked, Phone);
        masked = Digits().Replace(masked, Number);
        // A name word as a label writes it, hyphenated parts included (« Marie-Claire » is two words in « MARIE CLAIRE »).
        var names = nameWords.SelectMany(w => w.Split('-', '\'')).Concat(nameWords).Where(w => w.Length >= 3).ToHashSet(StringComparer.Ordinal);
        return names.Count == 0 ? masked : Word().Replace(masked, m => m.Value[0] != '[' && names.Contains(Fold(m.Value)) ? Name : m.Value);
    }

    /// <summary>Upper case, no accent: the form of <see cref="CategorySuggestions.NameWords"/>.</summary>
    private static string Fold(string word)
    {
        var text = new StringBuilder(word.Length);
        foreach (var c in word.ToUpperInvariant().Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                text.Append(c);
        return text.ToString().Normalize(NormalizationForm.FormC);
    }

    [GeneratedRegex(@"[\w.+-]+@[\w-]+(?:\.[\w-]+)+")]
    private static partial Regex EmailShape();

    /// <summary>Two letters, two check digits, then groups of four, spaced or not.</summary>
    [GeneratedRegex(@"\b[A-Z]{2}\d{2}(?: ?[A-Z0-9]{4}){2,7}(?: ?[A-Z0-9]{1,3})?\b", RegexOptions.IgnoreCase)]
    private static partial Regex IbanShape();

    [GeneratedRegex(@"(?<![\w+])(?:\+33 ?|0033 ?|0)[1-9](?:[ .-]?\d{2}){4}(?!\w)")]
    private static partial Regex FrenchPhone();

    [GeneratedRegex(@"(?<![\w+])\+\d{1,3}(?:[ .-]?\d{2,4}){2,5}(?!\w)")]
    private static partial Regex InternationalPhone();

    [GeneratedRegex(@"\d(?: ?\d){4,}")]
    private static partial Regex Digits();

    [GeneratedRegex(@"\[[A-Z]+\]|\p{L}+")]
    private static partial Regex Word();
}
