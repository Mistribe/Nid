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

    /// <summary>The majority <paramref name="Category"/>: <paramref name="Lines"/> of the <paramref name="Total"/> evidence lines agree.
    /// <paramref name="Basis"/> (#450, <see cref="SuggestionBases"/>): the pattern's own lines, those of patterns near it by meaning, or, with
    /// no evidence at all, the category's name, <paramref name="Similar"/> being that similarity in percent, or a model's guess, the
    /// probability in percent (0 for a model with no scores) on a worker of that <paramref name="Placement"/>
    /// (<c>local</c>, <c>remote</c>), or a cloud provider (<c>cloud</c>, #812).</summary>
    public sealed record Pick(string Pattern, bool Debit, Guid Category, int Lines, int Total, string Basis = SuggestionBases.History, int? Similar = null,
        string? Placement = null)
    {
        public int Agreement => Similar ?? Lines * 100 / Total;
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

    public const int Neighbours = 5;

    /// <summary>#450, cold start: how far the nearest category's name must lead the second.</summary>
    public const double MinMargin = 0.02;

    /// <summary>#450: the words of the household's names as a label writes them (upper case, no accent), three letters or more: a pattern
    /// holding one is never sent to a model.</summary>
    public static IReadOnlyList<string> NameWords(IEnumerable<string> names)
        => [.. names.SelectMany(n => TransactionLabel.Normalize(n).Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(w => w.Length >= 3).Distinct()];

    /// <summary>#450: an evidence pattern <paramref name="Near"/> the open <paramref name="Pattern"/>, by the cosine similarity of their
    /// embeddings.</summary>
    public sealed record Neighbour(string Pattern, string Near, double Similarity);

    /// <summary>
    /// #450, for an open pattern no evidence of its own decides: the lines of its <see cref="Neighbour"/>s at least
    /// <paramref name="minSimilarity"/> alike (the model's evaluation profile), in the same direction, decide as <see cref="Decide"/> does,
    /// on the same thresholds.
    /// </summary>
    public static Pick? ByMeaning(string pattern, bool debit, IEnumerable<Neighbour> neighbours, IEnumerable<Evidence> evidence, Func<Guid, bool> inUse,
        double minSimilarity)
    {
        var near = neighbours.Where(n => n.Pattern == pattern && n.Similarity >= minSimilarity).Select(n => n.Near).ToHashSet(StringComparer.Ordinal);
        var lines = evidence.Where(e => e.Debit == debit && near.Contains(e.Pattern)).GroupBy(e => e.Category)
            .Select(g => (Category: g.Key, Lines: g.Sum(e => e.Lines))).ToList();
        if (lines.Count == 0)
            return null;
        var top = lines.MaxBy(l => l.Lines);
        var total = lines.Sum(l => l.Lines);
        return total >= MinEvidence && top.Lines * 100 >= MinAgreementPercent * total && inUse(top.Category)
            ? new Pick(pattern, debit, top.Category, top.Lines, total, SuggestionBases.Meaning)
            : null;
    }

    /// <summary>#450: an open <paramref name="Pattern"/>'s similarity to a category's option text (<see cref="OptionText"/>), the category
    /// being of <paramref name="Debit"/>'s side (expense for debits, income for credits).</summary>
    public sealed record CategoryNeighbour(string Pattern, bool Debit, Guid Category, double Similarity);

    /// <summary>
    /// #450, cold start: a pattern no one's lines decide takes the category whose name it is nearest to, when that similarity reaches
    /// <paramref name="minSimilarity"/> (the model's profile) and leads the second nearest by <see cref="MinMargin"/>. <paramref name="near"/>
    /// holds the categories in use of each side.
    /// </summary>
    public static Pick? ByName(string pattern, bool debit, IEnumerable<CategoryNeighbour> near, double minSimilarity)
    {
        var ranked = near.Where(n => n.Pattern == pattern && n.Debit == debit).OrderByDescending(n => n.Similarity).Take(2).ToList();
        return ranked is [var top, ..] && top.Similarity >= minSimilarity && (ranked.Count == 1 || top.Similarity - ranked[1].Similarity >= MinMargin)
            ? new Pick(pattern, debit, top.Category, 0, 0, SuggestionBases.CategoryName, (int)Math.Round(top.Similarity * 100))
            : null;
    }

    /// <summary>#450: what a model reads of a category: « Parent › Name », or the name of a top category.</summary>
    public static string OptionText(string? parent, string name) => parent is null ? name : $"{parent} › {name}";
}

/// <summary>#450: what a suggestion rests on, as the API writes it.</summary>
public static class SuggestionBases
{
    public const string History = "history";
    public const string Meaning = "meaning";
    public const string CategoryName = "category_name";
    public const string Ai = "ai";
}
