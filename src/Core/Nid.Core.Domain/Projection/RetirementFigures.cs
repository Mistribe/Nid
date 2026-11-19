namespace Nid.Core.Domain.Projection;

/// <summary>
/// Retirement spec §3: at the retirement month <c>Month</c> (grid month kR), the monthly gap, the capital that would cover it at the
/// withdrawal rate, and the capital held then; all in today's money, unrounded. With a living cost the gap is what the expenses
/// (<c>Need</c>: the living cost, the commitments and the loan payments, as on the independence curve) ask beyond the income; without
/// one, <c>Need</c> is null and the gap is what the income loses.
/// </summary>
public sealed record RetirementFigures(
    int Month, decimal IncomeNow, decimal IncomeAtRetirement, decimal? Need, decimal Gap, decimal CapitalNeeded, decimal CapitalAtRetirement)
{
    /// <summary>Null when <paramref name="kR"/> is null or outside <c>[1, income.Count - 1]</c>, or there is no independence series.</summary>
    public static RetirementFigures? Of(
        IReadOnlyList<decimal> income, IReadOnlyList<decimal> inflation, IReadOnlyList<IndependencePoint> independence, int? kR,
        decimal withdrawalRate, decimal livingCost)
    {
        if (kR is not { } k || k < 1 || k > income.Count - 1 || independence.Count <= k) return null;
        var now = income[0];
        var atRetirement = income[k] / inflation[k];
        decimal? need = livingCost > 0m ? independence[k].ExpensesReal : null;
        var gap = Math.Max(0m, (need ?? now) - atRetirement);
        return new RetirementFigures(k, now, atRetirement, need, gap, gap * 12m / withdrawalRate, independence[k].Capital / inflation[k]);
    }
}
