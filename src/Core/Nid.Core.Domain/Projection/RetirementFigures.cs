namespace Nid.Core.Domain.Projection;

/// <summary>
/// Retirement spec §3: the income the household loses at the retirement month <c>Month</c> (grid month kR), the capital that would
/// cover it at the withdrawal rate, and the capital held then; all in today's money, unrounded.
/// </summary>
public sealed record RetirementFigures(
    int Month, decimal IncomeNow, decimal IncomeAtRetirement, decimal Gap, decimal CapitalNeeded, decimal CapitalAtRetirement)
{
    /// <summary>Null when <paramref name="kR"/> is null or outside <c>[1, income.Count - 1]</c>, or there is no independence series.</summary>
    public static RetirementFigures? Of(
        IReadOnlyList<decimal> income, IReadOnlyList<decimal> inflation, IReadOnlyList<IndependencePoint> independence, int? kR, decimal withdrawalRate)
    {
        if (kR is not { } k || k < 1 || k > income.Count - 1 || independence.Count <= k) return null;
        var now = income[0];
        var atRetirement = income[k] / inflation[k];
        var gap = Math.Max(0m, now - atRetirement);
        return new RetirementFigures(k, now, atRetirement, gap, gap * 12m / withdrawalRate, independence[k].Capital / inflation[k]);
    }
}
