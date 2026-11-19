using System.Globalization;
using System.Text;
using Nid.Banking.Contracts;

namespace Nid.Banking.Domain;

/// <summary>What a bank's descriptive fields of an account (Enable Banking's <c>details</c>, <c>product</c>, <c>name</c>) say to a person.</summary>
public static class AccountNames
{
    // ISO 20022 codes a bank may put where a description goes.
    private static readonly string[] Codes = ["CACC", "CASH", "CARD", "LOAN", "SVGS", "OTHR", "PRIV", "ORGA"];

    /// <summary>The first field, in the order given, that reads as a name, trimmed; empty when none does.</summary>
    public static string Pick(params string?[] fields)
        => fields.Select(f => f?.Trim()).FirstOrDefault(f => !string.IsNullOrEmpty(f) && !IsCode(f)) ?? "";

    /// <summary>A code rather than a name: one word with an underscore (<c>immediat_debit</c>), or an ISO 20022 code.</summary>
    public static bool IsCode(string value)
        => (value.Contains('_') && !value.Any(char.IsWhiteSpace)) || Codes.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>A card's debit as any of the fields words it (<c>immediat_debit</c>, « Débit différé », <c>deferred</c>); null when none says.</summary>
    public static CardDebit? Debit(params string?[] fields)
    {
        foreach (var field in fields.OfType<string>().Select(Plain))
        {
            if (field.Contains("immediat", StringComparison.Ordinal))
                return CardDebit.Immediate;
            if (field.Contains("differ", StringComparison.Ordinal) || field.Contains("deferred", StringComparison.Ordinal))
                return CardDebit.Deferred;
        }

        return null;
    }

    /// <summary>Lower case without accents: « Différé » as <c>differe</c>.</summary>
    private static string Plain(string value)
        => string.Concat(value.ToLowerInvariant().Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark));
}
