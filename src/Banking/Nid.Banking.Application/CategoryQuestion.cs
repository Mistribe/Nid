using Nid.Ai.Contracts;
using Nid.Banking.Domain;
using UnambitiousFx.Functional;
using UnambitiousFx.Functional.Failures;

namespace Nid.Banking.Application;

/// <summary>
/// #821 (<c>docs/ai/README.md</c> §7.2): how the <c>decide</c> model is asked a side's category, by the guess job and by its calibration
/// alike. In two steps, each list of 26 options at most so their ids are upper-case letters only (one list of 37 ran into « H » and « h »):
/// the side's top categories, each followed by its children's names, then « none of these »; then the children of the top scored best.
/// A top with children is never an answer itself: a line goes in its most precise category. Each request also carries texts that say
/// nothing, whose scores measure the model's lean (« Banque & frais » for any bank line), taken out of every score
/// (<see cref="DecideCalibration.Correct"/>).
/// </summary>
public static class CategoryQuestion
{
    /// <summary>The asking strategy, folded into a profile's version (<see cref="Version"/>): a profile measured on another way of asking
    /// is not applied, nor are the guesses kept under it.</summary>
    public const string Strategy = "2";

    public static string Version(string implementationVersion) => $"{implementationVersion}/{Strategy}";

    public const int MaxOptions = 26;

    /// <summary>A request's texts are 16 at most, these included.</summary>
    private static readonly string[] Blank = ["N/A", "[MASQUÉ]", "X"];

    /// <summary>The real texts one request takes, beside the content-free ones.</summary>
    public const int Batch = 13;

    /// <summary>A top category in use and its children in use, by name (masked).</summary>
    public sealed record Top(Guid Category, string Name, IReadOnlyList<(Guid Category, string Name)> Children);

    /// <summary>A side's categories in use as the two steps offer them, by name; null when a list would hold more than
    /// <see cref="MaxOptions"/>, « none of these » included: that side is not asked. A child whose parent is out of use is a top.</summary>
    public static IReadOnlyList<Top>? Tree(IReadOnlyList<(Guid Id, Guid? Parent, string Name)> side)
    {
        var ids = side.Select(c => c.Id).ToHashSet();
        var tops = side.Where(c => c.Parent is not { } parent || !ids.Contains(parent)).OrderBy(c => c.Name, StringComparer.Ordinal)
            .Select(t => new Top(t.Id, t.Name, [.. side.Where(c => c.Parent == t.Id).OrderBy(c => c.Name, StringComparer.Ordinal).Select(c => (c.Id, c.Name))]))
            .ToList();
        return tops.Count + 1 > MaxOptions || tops.Any(t => t.Children.Count > MaxOptions) ? null : tops;
    }

    /// <summary>What step 1 reads of a top: its name, then its children's, so « Logement » says it holds « Internet & téléphone ».</summary>
    public static string TopText(Top top)
    {
        var text = top.Children.Count == 0 ? top.Name : $"{top.Name} ({string.Join(", ", top.Children.Select(c => c.Name))})";
        return text.Length <= 200 ? text : text[..200];
    }

    /// <summary>The texts that say nothing, as a pattern of <paramref name="debit"/>'s side would be written.</summary>
    public static IReadOnlyList<string> ContentFree(bool debit) => [.. Blank.Select(b => CategoryGuesser.Text(b, b, null, debit, []))];

    /// <summary>What answered both steps, and one guess per text (its expectations left null), the categories as the tree has them.</summary>
    public sealed record Asked(DecideKey Key, string Placement, IReadOnlyList<DecideCalibration.Guess> Guesses);

    /// <summary>Asks <paramref name="texts"/> (<see cref="Batch"/> at most) of <paramref name="debit"/>'s side in two steps; the gateway's
    /// refusal as it is, or <c>ai.model_unavailable</c> when the steps were not answered by the same build.</summary>
    public static async Task<Result<Asked>> AskAsync(IDecisions decisions, IReadOnlyList<string> texts, bool debit, IReadOnlyList<Top> tops,
        string dataClass, IReadOnlyList<Guid> concerned, CancellationToken ct)
    {
        var first = await StepAsync(decisions, texts, debit, [.. tops.Select(TopText), CategoryGuesser.NoneOfThese], dataClass, concerned, ct);
        if (!first.TryGetValue(out var one))
            return BankingAccess.Failed<Asked, Step>(first);
        Guid? TopAt(int place) => place < tops.Count ? tops[place].Category : null;
        var guesses = one.Answers.Select(a => new DecideCalibration.Guess(null, a.Choice is { } c ? TopAt(c) : null,
            [.. a.Scores.Select(s => (TopAt(s.Key), s.Value))])).ToArray();

        // The second step under the top scored best (written, without scores), which no temperature changes.
        var under = guesses.Select((g, i) => (i, Top: g.Scores.Count > 0 ? DecideCalibration.Top(g.Scores, 1)?.Option : g.Choice))
            .Select(x => (x.i, Top: tops.FirstOrDefault(t => t.Category == x.Top)))
            .Where(x => x.Top is { Children.Count: > 0 }).GroupBy(x => x.Top!, x => x.i);
        foreach (var group in under)
        {
            var children = group.Key.Children;
            var second = await StepAsync(decisions, [.. group.Select(i => texts[i])], debit,
                [.. children.Select(c => CategorySuggestions.OptionText(group.Key.Name, c.Name))], dataClass, concerned, ct);
            if (!second.TryGetValue(out var two))
                return BankingAccess.Failed<Asked, Step>(second);
            if (two.Key != one.Key)
                return Result.Failure<Asked>(new Failure(JobCodes.ModelUnavailable, "The two steps were answered by two builds.", null));
            Guid? ChildAt(int place) => place < children.Count ? children[place].Category : null;
            foreach (var (i, a) in group.Zip(two.Answers))
                guesses[i] = guesses[i] with { Then = new DecideCalibration.Guess(null, a.Choice is { } c ? ChildAt(c) : null, [.. a.Scores.Select(s => (ChildAt(s.Key), s.Value))]) };
        }
        return Result.Success(new Asked(one.Key, one.Placement, guesses));
    }

    private sealed record Step(DecideKey Key, string Placement, IReadOnlyList<LlmDecision> Answers);

    /// <summary>One request: the content-free texts first, then <paramref name="texts"/>, whose scores come back corrected.</summary>
    private static async Task<Result<Step>> StepAsync(IDecisions decisions, IReadOnlyList<string> texts, bool debit, IReadOnlyList<string> options,
        string dataClass, IReadOnlyList<Guid> concerned, CancellationToken ct)
    {
        var free = ContentFree(debit);
        var decided = await decisions.DecideAsync(new LlmDecideRequest([.. free, .. texts], options, dataClass, concerned), ct);
        if (!decided.TryGetValue(out var result))
            return BankingAccess.Failed<Step, LlmDecisions>(decided);
        IReadOnlyList<IReadOnlyDictionary<int, double>> lean = [.. result.Decisions.Take(free.Count).Select(d => d.Scores)];
        return Result.Success(new Step(new DecideKey(result.Implementation, Version(result.Version), result.Model, result.Digest), result.Placement,
            [.. result.Decisions.Skip(free.Count).Select(d => d with { Scores = DecideCalibration.Correct(d.Scores, lean) })]));
    }
}
