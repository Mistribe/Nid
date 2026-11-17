namespace Nid.Core.Domain.Tax;

public static class TaxStepGroups
{
    public const string Income = "income";
    public const string Tax = "tax";
}

/// <summary>One line of the walk from the income to the tax: <c>From</c> is the running sum before it, <c>To</c> after it (a total spans 0 to its figure).</summary>
public sealed record TaxStep(string Code, string Group, decimal Amount, decimal From, decimal To);

public static class TaxSteps
{
    private const string Rounding = "rounding";

    /// <summary>
    /// <paramref name="raw"/> in order, income group first. Zero steps are left out; a <c>rounding</c> step absorbs what the clamps, the floor
    /// (income) and the rounding of the income tax (tax) take, so each group lands on its total. In the tax group the steps from
    /// <c>flat_tax</c> on come after the <c>income_tax</c> total.
    /// </summary>
    public static IReadOnlyList<TaxStep> Build(IEnumerable<(string Code, string Group, decimal Amount)> raw, decimal netTaxable, decimal incomeTax, decimal total)
    {
        var steps = new List<TaxStep>();
        var list = raw.Where(r => r.Amount != 0m).ToList();

        Walk(list.Where(r => r.Group == TaxStepGroups.Income), TaxStepGroups.Income, netTaxable, "net_taxable", 0m);

        var tax = list.Where(r => r.Group == TaxStepGroups.Tax).ToList();
        var split = tax.FindIndex(r => r.Code is "flat_tax" or "social_levies" or "high_income");
        var (before, after) = split < 0 ? (tax, []) : (tax[..split], tax[split..]);
        var sum = Walk(before, TaxStepGroups.Tax, incomeTax, "income_tax", 0m);
        Walk(after, TaxStepGroups.Tax, total, "total_tax", sum, rounding: false);
        return steps;

        // Returns the running sum after the group's total.
        decimal Walk(IEnumerable<(string Code, string Group, decimal Amount)> items, string group, decimal target, string totalCode, decimal start, bool rounding = true)
        {
            var running = start;
            foreach (var (code, g, amount) in items)
            {
                steps.Add(new TaxStep(code, g, amount, running, running + amount));
                running += amount;
            }
            if (rounding && running != target)
                steps.Add(new TaxStep(Rounding, group, target - running, running, target));
            steps.Add(new TaxStep(totalCode, group, target, 0m, target));
            return target;
        }
    }
}
