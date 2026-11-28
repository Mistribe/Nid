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

    private static Guess Scored(Guid? expected, double a, double b, double? none = null)
        => new(expected, null, [(A, a), (B, b), .. none is { } n ? [((Guid?)null, n)] : Array.Empty<(Guid?, double)>()]);

    [Fact]
    public void The_temperature_learnt_makes_the_probabilities_match_how_often_the_model_is_right()
    {
        // At temperature 1, A's score gives it 90 %: right 18 times out of 20, it is calibrated as it is.
        var calibrated = Enumerable.Range(0, 20).Select(i => Scored(i < 18 ? A : B, 0, -Math.Log(9))).ToList();
        Assert.Equal(1.0, Worker(calibrated, scoreless: false).Temperature);
        // Twice as sure of itself for the same record: its scores are halved.
        var overconfident = Enumerable.Range(0, 20).Select(i => Scored(i < 18 ? A : B, 0, -2 * Math.Log(9))).ToList();
        Assert.Equal(2.0, Worker(overconfident, scoreless: false).Temperature);

        Assert.Equal([0.9, 0.1], Softmax([0, -Math.Log(9)], 1).Select(p => Math.Round(p, 6)));
    }

    [Fact]
    public void The_lowest_probability_whose_suggestions_are_right_often_enough_is_kept()
    {
        // 40 sure and right, 10 unsure and wrong, 5 where « none of these » scores best: those are « don't know ».
        List<Guess> guesses =
        [
            .. Enumerable.Range(0, 40).Select(_ => Scored(A, 0, -5)),
            .. Enumerable.Range(0, 10).Select(_ => Scored(B, 0, -0.4)),
            .. Enumerable.Range(0, 5).Select(_ => Scored(null, -3, -4, none: 0)),
        ];

        var results = Worker(guesses, scoreless: false);

        var unsure = Top<Guid?>([(A, 0), (B, -0.4)], results.Temperature!.Value)!.Value.Probability;
        Assert.Equal(Math.Ceiling(unsure * 100) / 100, results.Guess!.Threshold);
        Assert.Equal((55, 40, 0, 15), (results.Cases, results.Guess.Shown, results.Guess.Wrong, results.Guess.DontKnow));
        Assert.Equal(55, results.Reliability!.Sum(b => b.Count));
        Assert.All(results.Reliability!, b => Assert.InRange(b.Confidence, 0, 1));
        Assert.Null(results.Neighbours);

        // Unsure and wrong as often as right: nothing is offered.
        Assert.Null(Worker([.. Enumerable.Range(0, 40).Select(i => Scored(i % 2 == 0 ? A : B, 0, -0.1))], scoreless: false).Guess);
    }

    [Fact]
    public void Without_scores_every_option_written_is_shown_and_offered_only_when_right_often_enough()
    {
        List<Guess> right = [.. Enumerable.Range(0, 25).Select(_ => new Guess(A, A, [])), new Guess(null, null, []), new Guess(B, null, [])];

        var offered = Worker([.. right, new Guess(B, A, [])], scoreless: true);
        Assert.Equal((0, 26, 1, 2), (offered.Guess!.Threshold, offered.Guess.Shown, offered.Guess.Wrong, offered.Guess.DontKnow));
        Assert.Null(offered.Temperature);

        Assert.Null(Worker([.. right, new Guess(B, A, []), new Guess(null, A, [])], scoreless: true).Guess);
    }

    [Fact]
    public void The_lean_a_model_shows_on_texts_that_say_nothing_is_taken_out_of_its_scores()
    {
        // Whatever it reads, the model gives option 1 80 % and option 0 20 %; option 2 never makes its likeliest on those texts.
        IReadOnlyList<IReadOnlyDictionary<int, double>> free =
        [
            new Dictionary<int, double> { [0] = Math.Log(0.2), [1] = Math.Log(0.8) },
            new Dictionary<int, double> { [0] = Math.Log(0.2), [1] = Math.Log(0.8) },
        ];

        var corrected = Correct(new Dictionary<int, double> { [0] = Math.Log(0.4), [1] = Math.Log(0.5), [2] = Math.Log(0.1) }, free);

        // 0.4 / 0.2 against 0.5 / 0.8: option 0 now leads. Option 2 had at most the lowest probability scored there, 20 %.
        Assert.Equal([.. new[] { 2, 0.625, 0.5 }.Select(p => Math.Round(Math.Log(p), 6))], corrected.OrderBy(c => c.Key).Select(c => Math.Round(c.Value, 6)));
        // No content-free answer scored anything: the scores as they are.
        Assert.Equal(Math.Log(0.4), Correct(new Dictionary<int, double> { [0] = Math.Log(0.4) }, [new Dictionary<int, double>()])[0]);
    }

    [Fact]
    public void In_two_steps_the_probability_is_the_top_s_times_the_child_s()
    {
        // Step 1: A (a top) at 90 %; step 2, among A's children, B at 50 %.
        var guess = new Guess(A, null, [(A, 0), ((Guid?)null, -Math.Log(9))], Then: new Guess(B, null, [(B, 0), (Guid.NewGuid(), 0)]));

        var pick = Pick(guess, 1)!.Value;

        Assert.Equal((B, 0.45), (pick.Option, Math.Round(pick.Probability, 6)));
        Assert.Equal(B, guess.Final);
        // No second step (« none of these », or a top with no children): the first step's.
        Assert.Equal(((Guid?)A, 0.9), Round(Pick(guess with { Then = null }, 1)!.Value));
        Assert.Null(Pick(new Guess(A, null, []), 1));

        static (Guid?, double) Round((Guid? Option, double Probability) p) => (p.Option, Math.Round(p.Probability, 6));
    }

    [Fact]
    public void Two_steps_are_calibrated_on_the_expected_top_then_on_the_expected_child_under_it()
    {
        // Each step at 90 % at temperature 1: the top right 90 times out of 100, the child right 81 times out of those 90. A case whose top
        // was wrong has its child outside the options of the second step, which then teaches nothing: calibrated as it is.
        var child = Guid.NewGuid();
        var other = Guid.NewGuid();
        Guess Case(int i) => new(i < 90 ? A : B, null, [(A, 0), (B, -Math.Log(9))],
            Then: new Guess(i < 81 ? child : i < 90 ? other : Guid.NewGuid(), null, [(child, 0), (other, -Math.Log(9))]));

        var results = Worker([.. Enumerable.Range(0, 100).Select(Case)], scoreless: false);

        Assert.Equal(1.0, results.Temperature);
        // 0.9 × 0.9 for each, right 81 times out of 100.
        var bin = Assert.Single(results.Reliability!);
        Assert.Equal((0.81, 0.81, 100), (Math.Round(bin.Confidence, 6), Math.Round(bin.Accuracy, 6), bin.Count));

        // Without scores: the option written at the last step asked.
        var written = Worker([.. Enumerable.Range(0, 25).Select(_ => new Guess(A, A, [], Then: new Guess(child, child, [])))], scoreless: true);
        Assert.Equal((25, 0), (written.Guess!.Shown, written.Guess.Wrong));
    }

    [Fact]
    public void A_model_that_cannot_tell_the_cases_apart_is_not_offered()
    {
        var cases = Enumerable.Range(0, 30).Select(i => new Case($"CASE {i}", true, i % 2 == 0 ? A : null)).ToList();
        var results = Neighbours(cases, [new("A ONE", true, A, 2)], [new Option(A, true, "Name A")], _ => At(0));
        Assert.Equal((null, null), (results.WithHistory, results.Cold));
    }
}
