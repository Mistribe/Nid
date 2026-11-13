using Nid.Childcare.Application;
using Nid.Childcare.Contracts;
using Nid.Core.Contracts;
using NodaTime;
using static Nid.Childcare.Tests.Fixture;

namespace Nid.Childcare.Tests;

/// <summary>#411: the year summary of an arrangement, from its validated statements.</summary>
public class ChildcareYearTests
{
    private static readonly ItemId Item = new(Guid.NewGuid());

    private static MonthFigures Figures(decimal netPay, decimal upkeep, decimal meals, decimal cmg)
        => new(new PajemploiDeclaration(165, 16, null, 0m, 0m, netPay, upkeep, meals, 5m), netPay, 0m, 0m, 0m, cmg,
            netPay + upkeep + meals + 5m - cmg, netPay + upkeep - cmg, 0);

    private static Statement Validated(int y, int m, decimal netPay, decimal upkeep, decimal meals, decimal cmg)
        => new(Item, new YearMonth(y, m), true, 5m, cmg, [], Figures(netPay, upkeep, meals, cmg), 1);

    private static Statement Draft(int y, int m) => new(Item, new YearMonth(y, m), false, 0m, null, [], null, 1);

    [Fact]
    public void Totals_sum_the_validated_months_of_the_year_only()
    {
        var statements = new[]
        {
            Validated(2025, 12, 500m, 60m, 40m, 100m), Validated(2026, 1, 626.37m, 64.11m, 56m, 120.50m),
            Validated(2026, 2, 600m, 70m, 50m, 130m), Draft(2026, 3), Validated(2027, 1, 999m, 1m, 1m, 1m),
        };

        var counted = ChildcareYear.Counted(statements, 2026);
        var totals = ChildcareYear.Totals(counted);

        Assert.Equal([new YearMonth(2026, 1), new YearMonth(2026, 2)], counted.Select(s => s.Month));
        Assert.Equal(new YearTotals(1226.37m, 250.50m, 134.11m, 106m, 1109.98m), totals);
        // The retained cost is the tax base the tax estimate sums: net pay + upkeep − CMG, meals and mileage left out.
        Assert.Equal(counted.Sum(s => s.Figures!.TaxBase), totals.TaxBase);
        Assert.Equal(totals.NetPay + totals.Upkeep - totals.Cmg, totals.TaxBase);
    }

    [Fact]
    public void A_year_without_validated_month_sums_nothing()
        => Assert.Equal(new YearTotals(0m, 0m, 0m, 0m, 0m), ChildcareYear.Totals(ChildcareYear.Counted([Draft(2026, 1)], 2026)));

    [Fact]
    public void Months_not_validated_are_the_year_s_months_without_a_validated_statement()
    {
        var statements = new[] { Validated(2026, 9, 1m, 0m, 0m, 0m), Draft(2026, 10) };

        // From the start in September 2026 to today's month, in the year asked.
        Assert.Equal([new YearMonth(2026, 10), new YearMonth(2026, 11), new YearMonth(2026, 12)],
            ChildcareYear.NotValidated(D(2026, 9, 1), null, D(2027, 2, 10), statements, 2026));
        Assert.Equal([new YearMonth(2027, 1), new YearMonth(2027, 2)], ChildcareYear.NotValidated(D(2026, 9, 1), null, D(2027, 2, 10), statements, 2027));
        // An ended arrangement has no month past its end.
        Assert.Equal([new YearMonth(2026, 10)], ChildcareYear.NotValidated(D(2026, 9, 1), D(2026, 10, 31), D(2027, 2, 10), statements, 2026));
    }

    [Fact]
    public void Projected_counts_each_month_left_at_the_mean_of_the_validated_ones()
    {
        var statements = new[] { Validated(2026, 9, 100m, 0m, 0m, 0m), Validated(2026, 10, 201m, 0m, 0m, 0m), Draft(2026, 11) };

        // The draft November and December, at (100 + 201) / 2.
        Assert.Equal([(new YearMonth(2026, 11), 150.50m), (new YearMonth(2026, 12), 150.50m)],
            ChildcareYear.Projected(D(2026, 9, 1), null, D(2026, 11, 10), statements, 2026));
        // The contract's end stops it.
        Assert.Equal([(new YearMonth(2026, 11), 150.50m)], ChildcareYear.Projected(D(2026, 9, 1), D(2026, 11, 30), D(2026, 11, 10), statements, 2026));
    }

    [Fact]
    public void Projected_is_empty_for_a_closed_year_or_without_a_validated_month()
    {
        var statements = new[] { Validated(2026, 9, 100m, 0m, 0m, 0m) };

        Assert.Empty(ChildcareYear.Projected(D(2026, 9, 1), null, D(2027, 2, 10), statements, 2026));
        Assert.Empty(ChildcareYear.Projected(D(2026, 9, 1), null, D(2026, 11, 10), [Draft(2026, 9)], 2026));
    }

    [Fact]
    public void Projected_without_a_validated_month_counts_each_month_at_the_estimate()
    {
        // September to December, none validated, each at the month computed from the contract.
        Assert.Equal([(new YearMonth(2026, 9), 80m), (new YearMonth(2026, 10), 80m), (new YearMonth(2026, 11), 80m), (new YearMonth(2026, 12), 80m)],
            ChildcareYear.Projected(D(2026, 9, 1), null, D(2026, 11, 10), [Draft(2026, 9)], 2026, estimate: 80m));
        Assert.Empty(ChildcareYear.Projected(D(2026, 9, 1), null, D(2027, 2, 10), [], 2026, estimate: 80m));
    }

    [Fact]
    public void The_year_just_ended_is_the_default_from_january()
    {
        var years = ChildcareYear.Years(D(2025, 9, 1), null, D(2026, 1, 5));
        Assert.Equal([2026, 2025], years);
        Assert.Equal(2025, ChildcareYear.DefaultYear(years, D(2026, 1, 5)));
        Assert.Equal(2025, ChildcareYear.DefaultYear(years, D(2026, 11, 5)));
        // An arrangement that started this year has no year just ended: its own year.
        Assert.Equal(2026, ChildcareYear.DefaultYear(ChildcareYear.Years(D(2026, 3, 1), null, D(2026, 11, 5)), D(2026, 11, 5)));
    }

    [Fact]
    public void The_box_follows_the_child_s_rank_eldest_first()
    {
        PersonId Child() => new(Guid.NewGuid());
        var (eldest, second, third, fourth, unknown) = (Child(), Child(), Child(), Child(), Child());
        IReadOnlyList<(PersonId, LocalDate?, string)> children =
        [
            (second, D(2023, 5, 1), "Paul"), (unknown, null, "Alice"), (eldest, D(2021, 2, 3), "Zoé"), (third, D(2024, 1, 1), "Léa"), (second, D(2023, 5, 1), "Paul"),
        ];

        Assert.Equal("7GA", ChildcareYear.Box(eldest, children));
        Assert.Equal("7GB", ChildcareYear.Box(second, children));
        Assert.Equal("7GC", ChildcareYear.Box(third, children));
        Assert.Null(ChildcareYear.Box(unknown, children));
        Assert.Equal("7GA", ChildcareYear.Box(fourth, [(fourth, null, "Seul")]));
    }
}
