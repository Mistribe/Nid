namespace Nid.Core.Contracts;

/// <summary>
/// The banks' debt ratio (HCSF): the monthly loan payments, insurance included, over the net monthly income. One rule for the
/// purchase scenarios (<c>ScenarioCalculator</c>) and the loans overview (issue #402).
/// </summary>
public static class DebtRatio
{
    /// <summary>The ceiling banks apply today (HCSF, 35 %).</summary>
    public const decimal Ceiling = 0.35m;

    /// <summary>Null without a positive income: the ratio is unknown.</summary>
    public static decimal? Of(decimal payments, decimal income) => income > 0m ? payments / income : null;

    /// <summary>What the payments may still grow by under the ceiling, negative above it; null without a positive income.</summary>
    public static decimal? Room(decimal payments, decimal income) => income > 0m ? Ceiling * income - payments : null;
}
