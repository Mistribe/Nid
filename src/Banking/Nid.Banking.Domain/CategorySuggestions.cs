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

    /// <summary>The majority <paramref name="Category"/>: <paramref name="Lines"/> of the <paramref name="Total"/> evidence lines agree;
    /// <paramref name="ByMeaning"/> when they are lines of patterns near this one (#450), not of this pattern itself.</summary>
    public sealed record Pick(string Pattern, bool Debit, Guid Category, int Lines, int Total, bool ByMeaning = false)
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

    // shortcut: starting values for bge-m3 on two-word labels, set by hand; the evaluation set of #450 sets them per model.
    public const double MinSimilarity = 0.8;
    public const int Neighbours = 5;

    /// <summary>#450: the words of the household's names as a label writes them (upper case, no accent), three letters or more: a pattern
    /// holding one is never sent to a model.</summary>
    public static IReadOnlyList<string> NameWords(IEnumerable<string> names)
        => [.. names.SelectMany(n => TransactionLabel.Normalize(n).Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(w => w.Length >= 3).Distinct()];

    /// <summary>#450: an evidence pattern <paramref name="Near"/> the open <paramref name="Pattern"/>, by the cosine similarity of their
    /// embeddings.</summary>
    public sealed record Neighbour(string Pattern, string Near, double Similarity);

    /// <summary>
    /// #450, for an open pattern no evidence of its own decides: the lines of its <see cref="Neighbour"/>s at least
    /// <see cref="MinSimilarity"/> alike, in the same direction, decide as <see cref="Decide"/> does, on the same thresholds.
    /// </summary>
    public static Pick? ByMeaning(string pattern, bool debit, IEnumerable<Neighbour> neighbours, IEnumerable<Evidence> evidence, Func<Guid, bool> inUse)
    {
        var near = neighbours.Where(n => n.Pattern == pattern && n.Similarity >= MinSimilarity).Select(n => n.Near).ToHashSet(StringComparer.Ordinal);
        var lines = evidence.Where(e => e.Debit == debit && near.Contains(e.Pattern)).GroupBy(e => e.Category)
            .Select(g => (Category: g.Key, Lines: g.Sum(e => e.Lines))).ToList();
        if (lines.Count == 0)
            return null;
        var top = lines.MaxBy(l => l.Lines);
        var total = lines.Sum(l => l.Lines);
        return total >= MinEvidence && top.Lines * 100 >= MinAgreementPercent * total && inUse(top.Category)
            ? new Pick(pattern, debit, top.Category, top.Lines, total, ByMeaning: true)
            : null;
    }
}
