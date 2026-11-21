namespace Nid.Banking.Domain;

/// <summary>
/// #449: the category the household's own choices point at for a suggested pattern (<see cref="CategoryRules.Suggest"/>) and a direction.
/// Its evidence is the lines a person categorised (by hand, or the assistant's approved proposal), never a rule's, a hint's, a link's or a split's.
/// </summary>
public static class CategorySuggestions
{
    // ponytail: starting thresholds, to tune on the demo household's data.
    public const int MinEvidence = 2;
    public const int MinAgreementPercent = 80;

    /// <summary><paramref name="Lines"/> lines of <paramref name="Pattern"/>, debits or credits, a person put in <paramref name="Category"/>.</summary>
    public sealed record Evidence(string Pattern, bool Debit, Guid Category, int Lines);

    /// <summary>The majority <paramref name="Category"/>: <paramref name="Lines"/> of the <paramref name="Total"/> evidence lines agree.</summary>
    public sealed record Pick(string Pattern, bool Debit, Guid Category, int Lines, int Total)
    {
        public int Agreement => Lines * 100 / Total;
    }

    /// <summary>
    /// For each pattern and direction, its majority category when at least <see cref="MinEvidence"/> lines back the pattern, at least
    /// <see cref="MinAgreementPercent"/> % of them agree, and <paramref name="inUse"/> says the category is neither archived nor merged.
    /// </summary>
    public static IReadOnlyList<Pick> Decide(IEnumerable<Evidence> evidence, Func<Guid, bool> inUse)
        => [.. evidence.GroupBy(e => (e.Pattern, e.Debit))
            .Select(g => (Top: g.MaxBy(e => e.Lines)!, Total: g.Sum(e => e.Lines)))
            .Where(g => g.Total >= MinEvidence && g.Top.Lines * 100 >= MinAgreementPercent * g.Total && inUse(g.Top.Category))
            .Select(g => new Pick(g.Top.Pattern, g.Top.Debit, g.Top.Category, g.Top.Lines, g.Total))];
}
