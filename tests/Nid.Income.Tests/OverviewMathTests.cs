using Nid.Core.Contracts;
using Nid.Income.Domain;
using NodaTime;

namespace Nid.Income.Tests;

public class OverviewMathTests
{
    private static readonly LocalDate Today = D(2026, 10, 20);
    private static LocalDate D(int y, int m, int d) => new(y, m, d);
    private static Due Dd(LocalDate on, decimal? amount) => new(on, amount);

    private static OverviewInput In(string name = "x", Due[]? recurring = null, Due[]? oneOffs = null, decimal? now = 0m, decimal? oneYear = 0m, decimal? threeYears = 0m)
        => new(new ItemId(Guid.CreateVersion7()), name, "salary", recurring ?? [], oneOffs ?? [],
            new Dictionary<LocalDate, decimal?> { [Today] = now, [Today.PlusYears(-1)] = oneYear, [Today.PlusYears(-3)] = threeYears });

    private static OverviewWindow YearWindow => OverviewMath.Window(OverviewPeriod.Year, Today);

    [Fact]
    public void Year_window_covers_the_calendar_year_and_the_previous_one()
        => Assert.Equal(new OverviewWindow(D(2026, 1, 1), D(2026, 12, 31), D(2025, 1, 1), D(2025, 12, 31)), YearWindow);

    [Fact]
    public void Rolling_windows_end_with_the_current_month()
    {
        Assert.Equal(new OverviewWindow(D(2025, 11, 1), D(2026, 10, 31), D(2024, 11, 1), D(2025, 10, 31)),
            OverviewMath.Window(OverviewPeriod.Last12Months, Today));
        Assert.Equal(new OverviewWindow(D(2023, 11, 1), D(2026, 10, 31), D(2020, 11, 1), D(2023, 10, 31)),
            OverviewMath.Window(OverviewPeriod.ThreeYears, Today));
    }

    [Fact]
    public void A_yearly_bonus_is_a_spike_in_its_month_not_smoothed()
    {
        var months = OverviewMath.Months([In("Prime", [Dd(D(2026, 12, 15), 1800m)])], YearWindow, Today);
        Assert.Equal(12, months.Count);
        Assert.Equal(1800m, months[11].Total);
        Assert.All(months.Take(11), m => Assert.Equal(0m, m.Total));
        Assert.Single(months[11].BySource);
    }

    [Fact]
    public void Months_after_today_are_upcoming_and_elapsed_stops_at_today()
    {
        var input = In("Salaire", [Dd(D(2026, 10, 1), 100m), Dd(D(2026, 10, 28), 100m), Dd(D(2026, 11, 1), 100m)]);
        var months = OverviewMath.Months([input], YearWindow, Today);
        Assert.False(months[9].Upcoming);
        Assert.True(months[10].Upcoming);
        Assert.Equal(100m, OverviewMath.Figures([input], YearWindow, Today).Elapsed);
        Assert.Equal(300m, OverviewMath.Figures([input], YearWindow, Today).Total);
    }

    [Fact]
    public void Previous_year_sits_in_the_same_month()
    {
        var months = OverviewMath.Months([In("Salaire", [Dd(D(2025, 3, 1), 2000m)])], YearWindow, Today);
        Assert.Equal(2000m, months[2].PreviousYear);
    }

    [Fact]
    public void A_month_with_an_unconvertible_due_has_no_total()
    {
        var months = OverviewMath.Months([In("Salaire", [Dd(D(2026, 2, 1), 100m), Dd(D(2026, 2, 2), null)])], YearWindow, Today);
        Assert.Null(months[1].Recurring);
        Assert.Null(months[1].Total);
        Assert.Equal(0m, months[0].Total);
        Assert.Null(OverviewMath.Figures([In("Salaire", [Dd(D(2026, 2, 2), null)])], YearWindow, Today).Total);
    }

    [Fact]
    public void Changes_without_a_base_are_null()
    {
        var f = OverviewMath.Figures([In(now: 100m, oneYear: 0m)], YearWindow, Today);
        Assert.Null(f.MonthlyChange1y);
        var firstYear = OverviewMath.Figures([In("Salaire", [Dd(D(2026, 3, 1), 30_000m)])], YearWindow, Today);
        Assert.Equal(30_000m, firstYear.Total);
        Assert.Null(firstYear.ChangeVsPrevious); // spec §3: no history reads « — », not the whole total
        Assert.Null(firstYear.ChangeVsPreviousPct);
        var missing = new OverviewInput(new ItemId(Guid.CreateVersion7()), "x", "salary", [], [],
            new Dictionary<LocalDate, decimal?> { [Today] = 100m, [Today.PlusYears(-1)] = 50m });
        var g = OverviewMath.Figures([missing], YearWindow, Today);
        Assert.Equal(100.0m, g.MonthlyChange1y);
        Assert.Null(g.Change3y);
        Assert.Null(g.Change3yYearly);
    }

    [Fact]
    public void Change_over_three_years_gives_the_yearly_rate()
    {
        var f = OverviewMath.Figures([In(now: 6000m, oneYear: 5500m, threeYears: 5000m)], YearWindow, Today);
        Assert.Equal(6000m, f.MonthlyNow);
        Assert.Equal(20.0m, f.Change3y);
        Assert.Equal(6.3m, f.Change3yYearly);
    }

    [Fact]
    public void Change_against_the_previous_window_is_absolute_and_relative()
    {
        var f = OverviewMath.Figures([In("Salaire", [Dd(D(2025, 5, 1), 1000m), Dd(D(2026, 5, 1), 1250m)])], YearWindow, Today);
        Assert.Equal(1000m, f.PreviousTotal);
        Assert.Equal(250m, f.ChangeVsPrevious);
        Assert.Equal(25.0m, f.ChangeVsPreviousPct);
    }

    [Fact]
    public void Split_keeps_six_shares_and_merges_the_rest_into_other()
    {
        var inputs = Enumerable.Range(1, 8).Select(n => In($"S{n}", [Dd(D(2026, 1, 1), n * 100m)])).ToList();
        var shares = OverviewMath.Split(inputs, YearWindow);
        Assert.Equal(7, shares.Count);
        Assert.Equal("other", shares[^1].Kind);
        Assert.Null(shares[^1].Item);
        Assert.Equal(300m, shares[^1].Total);
        Assert.Equal("S8", shares[0].Name);
        Assert.Equal(3600m, shares.Sum(s => s.Total));
    }

    [Fact]
    public void One_offs_form_one_share_of_their_own()
    {
        var inputs = new[]
        {
            In("Salaire", [Dd(D(2026, 1, 1), 900m)], [Dd(D(2026, 6, 1), 100m)]),
            In("Loyer", [], [Dd(D(2026, 7, 1), 100m)]),
        };
        var shares = OverviewMath.Split(inputs, YearWindow);
        var oneOff = Assert.Single(shares, s => s.Kind == "one_off");
        Assert.Equal(200m, oneOff.Total);
        Assert.Equal(18.2m, oneOff.Percent);
        Assert.Equal(200m / 12, oneOff.PerMonth);
        Assert.Equal(900m, shares[0].Total);
    }

    [Fact]
    public void Now_split_keeps_six_running_inputs_largest_first_and_merges_the_rest_into_other()
    {
        var inputs = Enumerable.Range(1, 8).Select(n => In($"S{n}", now: n * 100m)).Append(In("Fini", now: 0m)).ToList();
        var shares = OverviewMath.NowSplit(inputs, Today);
        Assert.Equal(["S8", "S7", "S6", "S5", "S4", "S3", "other"], shares.Select(s => s.Name));
        var other = shares[^1];
        Assert.Null(other.Item);
        Assert.Equal("other", other.Kind);
        Assert.Equal(300m, other.PerMonth);
        Assert.Equal(8.3m, other.Percent);
        Assert.Equal(22.2m, shares[0].Percent);
    }

    [Fact]
    public void Now_split_is_empty_when_a_monthly_amount_is_unconvertible_or_nothing_runs()
    {
        Assert.Empty(OverviewMath.NowSplit([In("A", now: 100m), In("B", now: null)], Today));
        Assert.Empty(OverviewMath.NowSplit([In("A", now: 0m)], Today));
    }

    [Fact]
    public void Split_is_empty_when_a_due_in_the_window_is_unconvertible()
    {
        var inputs = new[] { In("A", [Dd(D(2026, 1, 1), 100m)]), In("B", [Dd(D(2026, 2, 1), null)]) };
        Assert.Empty(OverviewMath.Split(inputs, YearWindow));
        Assert.Empty(OverviewMath.Split([In("A", [Dd(D(2026, 1, 1), 100m)], [Dd(D(2026, 3, 1), null)])], YearWindow));
    }

    [Fact]
    public void A_source_with_an_unconvertible_due_is_left_out_of_the_month_detail()
    {
        var inputs = new[] { In("A", [Dd(D(2026, 2, 1), 100m)]), In("B", [Dd(D(2026, 2, 1), 50m), Dd(D(2026, 2, 5), null)]) };
        var by = OverviewMath.Months(inputs, YearWindow, Today)[1].BySource;
        Assert.Equal("A", Assert.Single(by).Name);
    }

    [Fact]
    public void Split_ignores_shares_without_income_in_the_window()
    {
        var inputs = Enumerable.Range(1, 6).Select(n => In($"S{n}", [Dd(D(2026, 1, 1), n * 100m)]))
            .Append(In("Ended", [Dd(D(2024, 1, 1), 100m)])).ToList();
        var shares = OverviewMath.Split(inputs, YearWindow);
        Assert.Equal(6, shares.Count);
        Assert.DoesNotContain(shares, s => s.Kind == "other" || s.Name == "Ended");
    }

    [Fact]
    public void Three_year_window_keeps_previous_year_one_year_back()
    {
        var w = OverviewMath.Window(OverviewPeriod.ThreeYears, Today);
        var months = OverviewMath.Months([In("A", [Dd(D(2022, 11, 10), 70m), Dd(D(2020, 11, 10), 5m)])], w, Today);
        Assert.Equal(70m, months[0].PreviousYear);
    }

    [Fact]
    public void An_unconvertible_monthly_amount_today_gives_no_figure()
        => Assert.Null(OverviewMath.Figures([In(now: null)], YearWindow, Today).MonthlyNow);

    [Fact]
    public void A_change_needs_both_amounts_and_a_non_zero_start()
    {
        Assert.Equal(12.5m, OverviewMath.ChangePct(2250m, 2000m));
        Assert.Null(OverviewMath.ChangePct(2250m, 0m));
        Assert.Null(OverviewMath.ChangePct(null, 2000m));
    }
}
