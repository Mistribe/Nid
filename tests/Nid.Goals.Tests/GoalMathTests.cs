using Nid.Goals.Contracts;
using Nid.Goals.Domain;
using NodaTime;

namespace Nid.Goals.Tests;

public class GoalMathTests
{
    private static readonly LocalDate Today = D(2026, 10, 23);
    private static LocalDate D(int y, int m, int d) => new(y, m, d);
    private static GoalProgress Ev(decimal target, decimal reached, decimal? pace, LocalDate? date)
        => GoalMath.Evaluate(target, reached, pace, date, Today)!;

    [Fact]
    public void Reached_goal()
    {
        var g = Ev(1000, 1000, 50, D(2027, 6, 30));
        Assert.Equal(GoalStatuses.Reached, g.Status);
        Assert.Equal(100.0m, g.ProgressPct);
        Assert.Equal(0m, g.Remaining);
        Assert.Equal(0m, g.MonthlyNeeded);
        Assert.Null(g.ProjectedOn);
        Assert.Equal(50m, g.MonthlyPace);
    }

    [Fact]
    public void Reached_beyond_target_caps_progress_only()
    {
        var g = Ev(1000, 1250.555m, null, null);
        Assert.Equal(100.0m, g.ProgressPct);
        Assert.Equal(1250.56m, g.Reached);
        Assert.Equal(0m, g.Remaining);
        Assert.Equal(GoalStatuses.Reached, g.Status);
    }

    [Fact]
    public void On_track_when_projection_is_before_target_date()
    {
        var g = Ev(10000, 4000, 1000, D(2027, 6, 30));
        Assert.Equal(6000m, g.Remaining);
        Assert.Equal(750.00m, g.MonthlyNeeded);
        Assert.Equal(D(2027, 4, 23), g.ProjectedOn);
        Assert.Equal(GoalStatuses.OnTrack, g.Status);
        Assert.Equal(40.0m, g.ProgressPct);
    }

    [Fact]
    public void Behind_when_projection_is_after_target_date()
    {
        var g = Ev(10000, 4000, 500, D(2027, 6, 30));
        Assert.Equal(D(2027, 10, 23), g.ProjectedOn);
        Assert.Equal(GoalStatuses.Behind, g.Status);
        Assert.Equal(750.00m, g.MonthlyNeeded);
    }

    [Fact]
    public void Projection_on_target_date_is_on_track()
        => Assert.Equal(GoalStatuses.OnTrack, Ev(10000, 4000, 1000, D(2027, 4, 23)).Status);

    [Fact]
    public void Projection_rounds_months_up()
        => Assert.Equal(D(2027, 7, 23), Ev(10000, 4000, 700, null).ProjectedOn);

    [Fact]
    public void Past_due_needs_everything_now()
    {
        var g = Ev(1000, 400, 50, D(2026, 9, 30));
        Assert.Equal(GoalStatuses.PastDue, g.Status);
        Assert.Equal(600m, g.MonthlyNeeded);
        Assert.Equal(D(2027, 10, 23), g.ProjectedOn);
    }

    [Fact]
    public void Target_date_today_counts_one_month()
    {
        var g = Ev(1000, 400, null, Today);
        Assert.Equal(600m, g.MonthlyNeeded);
        Assert.Equal(GoalStatuses.Behind, g.Status);
    }

    [Fact]
    public void Less_than_a_month_counts_one_month()
        => Assert.Equal(600m, Ev(1000, 400, null, D(2026, 11, 10)).MonthlyNeeded);

    [Fact]
    public void No_date_has_no_monthly_needed()
    {
        var g = Ev(1000, 400, 100, null);
        Assert.Equal(GoalStatuses.NoDate, g.Status);
        Assert.Null(g.MonthlyNeeded);
        Assert.Equal(D(2027, 4, 23), g.ProjectedOn);
    }

    [Fact]
    public void No_pace_no_projection()
    {
        var g = Ev(1000, 400, null, null);
        Assert.Null(g.ProjectedOn);
        Assert.Null(g.MonthlyPace);
        Assert.Equal(GoalStatuses.NoDate, g.Status);
    }

    [Fact]
    public void Negative_pace_is_behind_without_projection()
    {
        var g = Ev(10000, 4000, -100, D(2027, 6, 30));
        Assert.Null(g.ProjectedOn);
        Assert.Equal(GoalStatuses.Behind, g.Status);
        Assert.Equal(750m, g.MonthlyNeeded);
    }

    [Fact]
    public void Zero_pace_with_date_is_behind()
    {
        var g = Ev(10000, 4000, 0, D(2027, 6, 30));
        Assert.Equal(GoalStatuses.Behind, g.Status);
        Assert.Null(g.ProjectedOn);
    }

    [Fact]
    public void Zero_target_gives_nothing()
        => Assert.Null(GoalMath.Evaluate(0, 100, 10, null, Today));

    [Fact]
    public void Rounds_amounts_and_percent()
    {
        var g = Ev(3000, 1000.005m, 33.333m, null);
        Assert.Equal(1000.01m, g.Reached);
        Assert.Equal(33.3m, g.ProgressPct);
        Assert.Equal(33.33m, g.MonthlyPace);
        Assert.Equal(1999.99m, g.Remaining);
    }

    private static FundingInput F(decimal? value, decimal net12, int bp, bool closed = false) => new(value, net12, bp, closed);
    private static ProgressEntry E(int y, int m, int d, decimal amount) => new(D(y, m, d), amount);

    [Fact]
    public void Reached_sums_value_times_share()
        => Assert.Equal(7000m, GoalMath.ReachedFromEnvelopes([F(10000, 0, 5000), F(2000, 0, 10000)]));

    [Fact]
    public void Envelope_without_value_counts_zero()
        => Assert.Equal(500m, GoalMath.ReachedFromEnvelopes([F(null, 0, 10000), F(500, 0, 10000)]));

    [Fact]
    public void Closed_envelope_counts_nothing()
    {
        FundingInput[] closed = [F(1000, 1200, 10000, true)];
        Assert.Equal(0m, GoalMath.ReachedFromEnvelopes(closed));
        Assert.Equal(0m, GoalMath.PaceFromEnvelopes(closed));
    }

    [Fact]
    public void Pace_is_net_contributions_times_share_over_twelve()
        => Assert.Equal(0m, GoalMath.PaceFromEnvelopes([F(null, 2400, 5000), F(null, -1200, 10000)]));

    [Fact]
    public void Pace_with_single_envelope()
        => Assert.Equal(500m, GoalMath.PaceFromEnvelopes([F(null, 6000, 10000)]));

    [Fact]
    public void Free_goal_reached_is_latest_entry()
        => Assert.Equal(900m, GoalMath.ReachedFromEntries([E(2026, 1, 10, 100), E(2026, 9, 1, 900), E(2026, 5, 1, 500)]));

    [Fact]
    public void Free_goal_without_entries_reaches_zero()
    {
        Assert.Equal(0m, GoalMath.ReachedFromEntries([]));
        Assert.Null(GoalMath.PaceFromEntries([], Today));
    }

    [Fact]
    public void Single_entry_gives_no_pace()
        => Assert.Null(GoalMath.PaceFromEntries([E(2026, 10, 1, 100)], Today));

    [Fact]
    public void Pace_uses_entry_from_twelve_months_ago()
    {
        var pace = GoalMath.PaceFromEntries([E(2025, 6, 1, 0), E(2025, 10, 1, 1000), E(2026, 10, 1, 4000)], Today);
        Assert.Equal(250.17m, Math.Round(pace!.Value, 2));
    }

    [Fact]
    public void Pace_uses_first_entry_when_younger_than_a_year()
    {
        var pace = GoalMath.PaceFromEntries([E(2026, 4, 1, 1000), E(2026, 10, 1, 2200)], Today);
        Assert.Equal(199.59m, Math.Round(pace!.Value, 2));
    }

    [Fact]
    public void Pace_counts_at_least_one_month()
        => Assert.Equal(300m, GoalMath.PaceFromEntries([E(2026, 10, 1, 100), E(2026, 10, 11, 400)], Today));

    [Fact]
    public void Entries_all_older_than_a_year_give_no_pace()
        => Assert.Null(GoalMath.PaceFromEntries([E(2024, 1, 1, 100), E(2025, 1, 1, 500)], Today));

    [Fact]
    public void Emergency_target_is_months_times_cost()
        => Assert.Equal(7407m, GoalMath.EmergencyTarget(6, 1234.5m));

    [Fact]
    public void Free_share_is_what_is_left()
    {
        Assert.Equal(4000, GoalMath.FreeShareBp([6000]));
        Assert.Equal(0, GoalMath.FreeShareBp([6000, 4000]));
        Assert.Equal(0, GoalMath.FreeShareBp([7000, 5000]));
        Assert.Equal(10000, GoalMath.FreeShareBp([]));
    }

    [Fact]
    public void Tiny_pace_gives_no_projection()
    {
        var withDate = Ev(10000, 0, 0.0001m, D(2027, 6, 30));
        Assert.Null(withDate.ProjectedOn);
        Assert.Equal(GoalStatuses.Behind, withDate.Status);
        var noDate = Ev(10000, 0, 0.0001m, null);
        Assert.Null(noDate.ProjectedOn);
        Assert.Equal(GoalStatuses.NoDate, noDate.Status);
    }

    [Fact]
    public void Rounding_to_target_counts_as_reached()
    {
        var g = Ev(1000, 999.996m, null, null);
        Assert.Equal(1000.00m, g.Reached);
        Assert.Equal(0m, g.Remaining);
        Assert.Equal(GoalStatuses.Reached, g.Status);
        Assert.Equal(0m, g.MonthlyNeeded);
    }

    [Fact]
    public void Negative_reached_shows_zero_progress()
    {
        var g = Ev(1000, -50, null, null);
        Assert.Equal(0.0m, g.ProgressPct);
        Assert.Equal(1050m, g.Remaining);
    }
}
