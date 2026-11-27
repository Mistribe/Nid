using Nid.Banking.Domain;
using static Nid.Banking.Domain.DecideCalibration;

namespace Nid.Banking.Tests;

/// <summary>#450: a model's thresholds from the evaluation set: the lowest that keeps the wrong suggestions at 5 % at most.</summary>
public class DecideCalibrationTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid();

    [Fact]
    public void A_pick_of_a_case_no_category_fits_is_wrong_and_no_pick_is_dont_know()
    {
        var m = Count(0.8, [new Answer(A, A), new Answer(A, B), new Answer(null, A), new Answer(null, null), new Answer(B, null)]);
        Assert.Equal((5, 3, 1, 2, 2, 0.6), (m.Cases, m.Shown, m.Correct, m.Wrong, m.DontKnow, m.Coverage));
    }

    [Fact]
    public void The_wrong_rate_interval_is_wilson_95()
    {
        Assert.Equal((0, 0.161), Round(Wilson(0, 20)));
        Assert.Equal((0.009, 0.236), Round(Wilson(1, 20)));
        Assert.Equal((0, 1), Wilson(0, 0));

        static (double, double) Round((double Low, double High) i) => (Math.Round(i.Low, 3), Math.Round(i.High, 3));
    }

    [Fact]
    public void The_lowest_threshold_with_five_percent_wrong_at_most_over_twenty_shown_is_kept()
    {
        static Measure At(double t, int shown, int wrong) => new(t, 100, shown, shown - wrong, wrong, 100 - shown, 0, 1, shown / 100.0);

        Assert.Equal(0.6, Choose([At(0.7, 20, 0), At(0.5, 40, 3), At(0.6, 20, 1)])!.Threshold);
        // Too few shown, or too many wrong: not offered.
        Assert.Null(Choose([At(0.5, 19, 0), At(0.6, 40, 3)]));
        Assert.Equal([0.5, 0.51, 0.52], Grid(0.50, 0.52));
    }

    /// <summary>Texts on the unit circle at an angle, in degrees: their cosine similarity is the cosine of the angle between them.</summary>
    private static IReadOnlyList<float> At(double degrees) => [(float)Math.Cos(degrees * Math.PI / 180), (float)Math.Sin(degrees * Math.PI / 180)];

    [Fact]
    public void Nearest_neighbours_keep_the_threshold_past_the_cases_no_category_fits()
    {
        // 20 cases close to A's history and names (10°), 5 that no category fits a little further (30°); B is far (100°).
        var angles = new Dictionary<string, double> { ["A ONE"] = 0, ["A TWO"] = 0, ["B ONE"] = 100, ["Name A"] = 5, ["Name B"] = 95 };
        Case[] cases =
        [
            .. Enumerable.Range(0, 20).Select(i => { angles[$"CASE {i}"] = 10; return new Case($"CASE {i}", true, A); }),
            .. Enumerable.Range(0, 5).Select(i => { angles[$"NONE {i}"] = 30; return new Case($"NONE {i}", true, null); }),
        ];
        CategorySuggestions.Evidence[] history = [new("A ONE", true, A, 2), new("A TWO", true, A, 2), new("B ONE", true, B, 2)];

        var results = Neighbours(cases, history, [new Option(A, true, "Name A"), new Option(B, true, "Name B"), new Option(B, false, "Name A")],
            text => At(angles[text]));

        // With history: cos 30° = 0.866 still lets the wrong ones through at 0.86. On names: cos 25° = 0.906.
        Assert.Equal((0.87, 20, 0, 5), (results.WithHistory!.Threshold, results.WithHistory.Shown, results.WithHistory.Wrong, results.WithHistory.DontKnow));
        Assert.Equal((0.91, 20, 0), (results.Cold!.Threshold, results.Cold.Shown, results.Cold.Wrong));
        Assert.Equal((25, 5), (results.Cases, results.Neighbours));
    }

    [Fact]
    public void A_model_that_cannot_tell_the_cases_apart_is_not_offered()
    {
        var cases = Enumerable.Range(0, 30).Select(i => new Case($"CASE {i}", true, i % 2 == 0 ? A : null)).ToList();
        var results = Neighbours(cases, [new("A ONE", true, A, 2)], [new Option(A, true, "Name A")], _ => At(0));
        Assert.Equal((null, null), (results.WithHistory, results.Cold));
    }
}
