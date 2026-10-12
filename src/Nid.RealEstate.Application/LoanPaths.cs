using System.Globalization;
using UnambitiousFx.Functional.Failures;

namespace Nid.RealEstate.Application;

/// <summary>
/// Spec 3A2 §4: every 422 names its field in the body's own path. The engine names its fields after <c>LoanTerms</c>
/// (<c>principal</c>, <c>phases[i].x</c>, <c>insurances[i].x</c>, <c>fees[i].x</c>, <c>events[i].x</c>).
/// </summary>
internal static class LoanPaths
{
    public static string? FieldOf(Failure failure)
        => failure.Metadata is { } metadata && metadata.TryGetValue("field", out var field) ? field as string : null;

    public static Failure At(Failure failure, string field)
        => new(failure.Code, failure.Message, new Dictionary<string, object?> { ["field"] = field });

    /// <summary>
    /// The creation and contract bodies carry the engine's names at their top level, so its paths are theirs; only its
    /// <c>events[i]</c>, the stored repayments, become <c>repayments[i]</c>, i their rank by date then id — the detail's
    /// order, which <see cref="LoanReading.Terms"/> hands the engine.
    /// </summary>
    public static Failure ForContract(Failure failure)
        => Event(FieldOf(failure)) is { } e ? At(failure, $"repayments[{e.Index}]{e.Tail}") : failure;

    /// <summary>
    /// A removal (precision 7): the engine's <c>events[k]</c> ranks the repayments that remain; the detail's
    /// <c>repayments[i]</c> ranks the full stored list, which still holds the removed one, so <paramref name="stored"/> maps k to i.
    /// </summary>
    public static Failure ForRemoval(Failure failure, Func<int, int> stored)
        => Event(FieldOf(failure)) is { } e ? At(failure, $"repayments[{stored(e.Index)}]{e.Tail}") : failure;

    /// <summary>
    /// A repayment route (precision 7): the written repayment, <c>events[written]</c>, answers at its own fields; its
    /// penalty, <c>fees[penalty]</c>, at <c>penalty</c>; anything else it makes impossible (a later repayment above the
    /// balance) at <c>amount</c>.
    /// </summary>
    public static Failure ForRepayment(Failure failure, int written, int? penalty)
    {
        var field = FieldOf(failure);
        if (Event(field) is { } e && e.Index == written)
            return At(failure, e.Tail.TrimStart('.'));
        if (penalty is { } k && field is not null && field.StartsWith($"fees[{k}].", StringComparison.Ordinal))
            return At(failure, "penalty");
        return At(failure, "amount");
    }

    private static (int Index, string Tail)? Event(string? field)
    {
        if (field is null || !field.StartsWith("events[", StringComparison.Ordinal))
            return null;
        var close = field.IndexOf(']');
        return (int.Parse(field["events[".Length..close], CultureInfo.InvariantCulture), field[(close + 1)..]);
    }
}
