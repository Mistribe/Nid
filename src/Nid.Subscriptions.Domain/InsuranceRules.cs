using Nid.Subscriptions.Contracts;

namespace Nid.Subscriptions.Domain;

/// <summary>The member of an insurance a rule refused: each caller maps it to its own path style (API camelCase, file snake_case).</summary>
public enum InsuranceField
{
    Guarantees,
    GuaranteeLabel,
    GuaranteeCeiling,
    GuaranteeDeductible,
    InsuredLabel,
    PolicyNumber,
    InsurerContact,
}

/// <summary>A refused rule: stable code, message, the member and, for a guarantee's member, its position in the list.</summary>
public sealed record InsuranceIssue(string Code, string Message, InsuranceField Field, int? Index = null);

/// <summary>The limits of what a household types about a policy (design 2026-10-30-contract-terms-insurance §7).</summary>
public static class InsuranceRules
{
    public const int MaxTextLength = 200;
    public const int MaxGuarantees = 50;

    public static InsuranceIssue? Validate(InsuranceDetails details)
    {
        if (details.Guarantees.Count > MaxGuarantees)
            return new InsuranceIssue("subscriptions.too_many_guarantees", $"A policy has at most {MaxGuarantees} guarantees.", InsuranceField.Guarantees);

        for (var i = 0; i < details.Guarantees.Count; i++)
            if (string.IsNullOrWhiteSpace(details.Guarantees[i].Label))
                return new InsuranceIssue("subscriptions.guarantee_label", "A guarantee has a label.", InsuranceField.GuaranteeLabel, i);

        if (TooLong(details.InsuredLabel)) return TextLength(InsuranceField.InsuredLabel);
        if (TooLong(details.PolicyNumber)) return TextLength(InsuranceField.PolicyNumber);
        if (TooLong(details.InsurerContact)) return TextLength(InsuranceField.InsurerContact);

        for (var i = 0; i < details.Guarantees.Count; i++)
        {
            var g = details.Guarantees[i];
            if (TooLong(g.Label)) return TextLength(InsuranceField.GuaranteeLabel, i);
            if (TooLong(g.Ceiling)) return TextLength(InsuranceField.GuaranteeCeiling, i);
            if (TooLong(g.Deductible)) return TextLength(InsuranceField.GuaranteeDeductible, i);
        }

        return null;
    }

    private static bool TooLong(string? text) => text is { Length: > MaxTextLength };

    private static InsuranceIssue TextLength(InsuranceField field, int? index = null)
        => new("subscriptions.insurance_text_length", $"A text is at most {MaxTextLength} characters.", field, index);
}
