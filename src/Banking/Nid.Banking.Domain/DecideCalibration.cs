using static Nid.Banking.Domain.CategorySuggestions;

namespace Nid.Banking.Domain;

/// <summary>
/// #450 (<c>docs/ai/README.md</c> §7.3): how a model is calibrated on the evaluation set. Each threshold of a sweep is counted (shown,
/// correct, wrong, « don't know »); the lowest one whose wrong rate among the shown suggestions is at most <see cref="MaxWrongRate"/>,
/// over at least <see cref="MinShown"/> of them, is kept; none, and that mode is not offered for the model.
/// </summary>
public static class DecideCalibration
{
    public const string NearestNeighbours = "nearest-neighbours";
    public const string NearestNeighboursVersion = "1";

    public const double MaxWrongRate = 0.05;
    public const int MinShown = 20;

    /// <summary>One case at one threshold: what was <paramref name="Expected"/> (null: no category fits) and what was
    /// <paramref name="Picked"/> (null: « don't know »).</summary>
    public sealed record Answer(Guid? Expected, Guid? Picked);

    /// <summary>A threshold's counts over <paramref name="Cases"/> cases: a pick is shown, wrong when it is not the expected category
    /// (any pick of a case no category fits is wrong); <paramref name="WrongLow"/>–<paramref name="WrongHigh"/> is the wrong rate's
    /// Wilson 95 % interval, <paramref name="Coverage"/> the share of cases shown.</summary>
    public sealed record Measure(double Threshold, int Cases, int Shown, int Correct, int Wrong, int DontKnow, double WrongLow, double WrongHigh,
        double Coverage);

    public static Measure Count(double threshold, IReadOnlyCollection<Answer> answers)
    {
        var shown = answers.Count(a => a.Picked is not null);
        var correct = answers.Count(a => a.Picked is { } picked && picked == a.Expected);
        var (low, high) = Wilson(shown - correct, shown);
        return new Measure(threshold, answers.Count, shown, correct, shown - correct, answers.Count - shown, low, high,
            answers.Count == 0 ? 0 : (double)shown / answers.Count);
    }

    /// <summary>The lowest threshold that keeps the wrong rate at most <see cref="MaxWrongRate"/> over at least <see cref="MinShown"/>
    /// shown; null when none does.</summary>
    public static Measure? Choose(IEnumerable<Measure> sweep)
        => sweep.OrderBy(m => m.Threshold).FirstOrDefault(m => m.Shown >= MinShown && m.Wrong <= MaxWrongRate * m.Shown);

    /// <summary>The Wilson score interval, 95 %, of <paramref name="k"/> in <paramref name="n"/>.</summary>
    public static (double Low, double High) Wilson(int k, int n)
    {
        if (n == 0)
            return (0, 1);
        const double z2 = 1.96 * 1.96;
        var p = (double)k / n;
        var centre = (p + z2 / (2 * n)) / (1 + z2 / n);
        var half = 1.96 * Math.Sqrt(p * (1 - p) / n + z2 / (4.0 * n * n)) / (1 + z2 / n);
        return (Math.Max(0, centre - half), Math.Min(1, centre + half));
    }

    /// <summary>From <paramref name="from"/> to <paramref name="to"/> by 0.01.</summary>
    public static IReadOnlyList<double> Grid(double from, double to)
        => [.. Enumerable.Range(0, (int)Math.Round((to - from) * 100) + 1).Select(i => Math.Round(from + i / 100.0, 2))];

    public static double Cosine(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Count; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return na == 0 || nb == 0 ? 0 : dot / Math.Sqrt(na * nb);
    }

    /// <summary>An evaluation case: its suggested pattern, its direction and the category expected, if any fits.</summary>
    public sealed record Case(string Pattern, bool Debit, Guid? Expected);

    /// <summary>A category a case of <paramref name="Debit"/>'s side may take, as a model reads it (<see cref="OptionText"/>).</summary>
    public sealed record Option(Guid Category, bool Debit, string Text);

    /// <summary>What a calibration measured on <paramref name="Cases"/> cases. Nearest neighbours: the threshold kept with history
    /// (<see cref="ByMeaning"/>, over <paramref name="Neighbours"/> neighbours) and on the names alone (<see cref="ByName"/>), each
    /// null when that mode is not offered. A worker's <c>decide</c>: <paramref name="Guess"/> at the probability kept (null: not offered),
    /// the <paramref name="Temperature"/> learnt and the <paramref name="Reliability"/> of its probabilities.</summary>
    public sealed record Results(int Cases, int? Neighbours, Measure? WithHistory, Measure? Cold, Measure? Guess = null, double? Temperature = null,
        IReadOnlyList<Bin>? Reliability = null);

    /// <summary>One fifth of the probabilities: their mean, the share of those cases answered right, how many there were.</summary>
    public sealed record Bin(double Confidence, double Accuracy, int Count);

    /// <summary>A worker's answer to a case: the category <paramref name="Expected"/> (null: none fits), the one it wrote
    /// (<paramref name="Choice"/>, null for « none of these » or no option), and its raw score of each option it scored, « none of these »
    /// being the null category.</summary>
    public sealed record Guess(Guid? Expected, Guid? Choice, IReadOnlyList<(Guid? Category, double Raw)> Scores);

    /// <summary>The softmax of raw scores divided by <paramref name="temperature"/>: the probability of each.</summary>
    public static IReadOnlyList<double> Softmax(IReadOnlyList<double> raw, double temperature)
    {
        if (raw.Count == 0)
            return [];
        var max = raw.Max();
        var exp = raw.Select(r => Math.Exp((r - max) / temperature)).ToList();
        var sum = exp.Sum();
        return [.. exp.Select(e => e / sum)];
    }

    /// <summary>The option scored best and its probability at <paramref name="temperature"/>; null with no score.</summary>
    public static (T Option, double Probability)? Top<T>(IReadOnlyList<(T Option, double Raw)> scores, double temperature)
    {
        if (scores.Count == 0)
            return null;
        var p = Softmax([.. scores.Select(s => s.Raw)], temperature);
        var best = Enumerable.Range(0, scores.Count).MaxBy(i => p[i]);
        return (scores[best].Option, p[best]);
    }

    /// <summary>
    /// A worker's <c>decide</c> on the evaluation set. With scores: the temperature (0.25 to 4 by 0.05) that best predicts the expected
    /// option (« none of these » for a case no category fits), then the lowest calibrated probability, from 0.30 to 0.99, at which the
    /// option scored best is right often enough. Without (<paramref name="scoreless"/>): every option written is shown, offered only when
    /// that is right often enough.
    /// </summary>
    public static Results Worker(IReadOnlyList<Guess> guesses, bool scoreless)
    {
        if (scoreless)
        {
            var all = Count(0, [.. guesses.Select(g => new Answer(g.Expected, g.Choice))]);
            return new Results(guesses.Count, null, null, null, Guess: Choose([all]));
        }

        var scored = guesses.Where(g => g.Scores.Count > 0).ToList();
        // An expected option the model did not score among its likeliest has no probability at any temperature: its loss is the same for all.
        var temperature = Enumerable.Range(0, 76).Select(i => Math.Round(0.25 + i * 0.05, 2)).MinBy(t => scored.Sum(g =>
        {
            var p = Softmax([.. g.Scores.Select(s => s.Raw)], t);
            var expected = p.Where((_, i) => g.Scores[i].Category == g.Expected).DefaultIfEmpty(0).Single();
            return -Math.Log(Math.Max(expected, 1e-9));
        }));
        var tops = guesses.Select(g => (g.Expected, Top: Top(g.Scores, temperature))).ToList();
        var guess = Choose(Grid(0.30, 0.99).Select(t => Count(t,
            [.. tops.Select(c => new Answer(c.Expected, c.Top is { Option: { } category, Probability: var p } && p >= t ? category : null))])));
        var reliability = tops.Where(c => c.Top is not null).GroupBy(c => Math.Min(4, (int)(c.Top!.Value.Probability * 5))).OrderBy(b => b.Key)
            .Select(b => new Bin(b.Average(c => c.Top!.Value.Probability), b.Average(c => c.Top!.Value.Option == c.Expected ? 1.0 : 0.0), b.Count())).ToList();
        return new Results(guesses.Count, null, null, null, guess, temperature, reliability);
    }

    /// <summary>
    /// Nearest neighbours on the evaluation set, from the embeddings of its texts (<paramref name="vector"/>): with the set's
    /// <paramref name="history"/> as evidence, its <see cref="Neighbours"/> nearest patterns vote as on a household's lines; on the
    /// names alone, the nearest option of the case's side, as a household with no history. Each swept from 0.50 to 0.95.
    /// </summary>
    public static Results Neighbours(IReadOnlyList<Case> cases, IReadOnlyList<Evidence> history, IReadOnlyList<Option> options,
        Func<string, IReadOnlyList<float>> vector)
    {
        var known = history.Select(h => h.Pattern).Distinct().ToList();
        var neighbours = cases.Select(c => c.Pattern).Distinct().ToDictionary(p => p, p => known.Where(k => k != p)
            .Select(k => new Neighbour(p, k, Cosine(vector(p), vector(k)))).OrderByDescending(n => n.Similarity).Take(CategorySuggestions.Neighbours).ToList());
        var names = cases.Select(c => (c.Pattern, c.Debit)).Distinct().ToDictionary(c => c, c => options.Where(o => o.Debit == c.Debit)
            .Select(o => new CategoryNeighbour(c.Pattern, c.Debit, o.Category, Cosine(vector(c.Pattern), vector(o.Text)))).ToList());
        var grid = Grid(0.50, 0.95);
        var withHistory = Choose(grid.Select(t => Count(t,
            [.. cases.Select(c => new Answer(c.Expected, ByMeaning(c.Pattern, c.Debit, neighbours[c.Pattern], history, _ => true, t)?.Category))])));
        var cold = Choose(grid.Select(t => Count(t,
            [.. cases.Select(c => new Answer(c.Expected, ByName(c.Pattern, c.Debit, names[(c.Pattern, c.Debit)], t)?.Category))])));
        return new Results(cases.Count, CategorySuggestions.Neighbours, withHistory, cold);
    }
}
