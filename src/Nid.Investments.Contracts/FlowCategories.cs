using Nid.Core.Contracts;

namespace Nid.Investments.Contracts;

/// <summary>Passive-income spec C2: a withdrawal or income flow is classified as income by its category; no calculation reads these.</summary>
public static class FlowCategories
{
    public const string Transfer = "transfer", Dividend = "dividend", Interest = "interest", Distribution = "distribution";

    public static readonly IReadOnlyList<string> Income = [Dividend, Interest, Distribution];

    public static bool IsIncome(string? category) => category is not null && Income.Contains(category);

    /// <summary>Spec C2: the refusal message, or null when the category is allowed on this envelope and flow kind.</summary>
    public static string? Refusal(EnvelopeKind envelope, FlowKind kind, string? category)
    {
        if (!IsIncome(category))
            return null;
        if (envelope == EnvelopeKind.Savings)
            return kind == FlowKind.Income
                ? "A savings account's income is not classified: it takes no income category."
                : "A savings account's withdrawals already count as its income: they take no income category.";
        return kind is FlowKind.Withdrawal or FlowKind.Income ? null
            : $"'{category}' only classifies a withdrawal or an income.";
    }
}
