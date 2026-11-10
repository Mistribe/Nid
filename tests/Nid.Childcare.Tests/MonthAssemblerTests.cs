using Nid.Childcare.Application;
using Nid.Childcare.Contracts;
using Nid.Childcare.Domain;
using Nid.Core.Contracts;
using NodaTime;
using static Nid.Childcare.Tests.Fixture;

namespace Nid.Childcare.Tests;

/// <summary>Childcare decision 25: a month computed from data in memory, as an import re-checks a statement before anything is stored.</summary>
public class MonthAssemblerTests
{
    private static readonly ItemId Item = new(Guid.NewGuid());

    private static MonthTerms TermsOf(YearMonth month)
        => MonthTerms.For([new Terms(Item, D(2025, 9, 1), null, 4.30m, null, 0.10m, 3.80m, 3.20m, [new WeekType("Semaine type", ProgrammedWeeks, Schedule)])], [], [], month);

    [Fact]
    public void Same_figures_as_the_store_path()
    {
        var month = new YearMonth(2025, 9);
        var terms = TermsOf(month);
        var days = MonthAssembler.Prepared(terms).Select(d => d.Date.Day switch
        {
            8 => d with { Present = false, Arrival = null, Departure = null, Lunch = false, Absence = AbsenceReason.ChildSick },
            9 => d with { Departure = new LocalTime(19, 0) },
            _ => d,
        }).ToList();
        var figures = MonthAssembler.Evaluate(terms, days, [], [], null, 0m, 0m).Figures!;
        // 1.5 h extra paid as worked, declared as 2 h, the half hour taken out of the normal hours.
        Assert.Equal(598.02m, figures.Declaration.NetPay);
        Assert.Equal(2m, figures.Declaration.ComplementaryHours);
        Assert.Equal(137, figures.Declaration.NormalHours); // (598.02 − 2 × 4.30) ÷ 4.30 = 137.07
        Assert.Equal(15, figures.Declaration.ActivityDays);
    }

    /// <summary>Friday 2026-05-01 belongs to April's last week: the hours really worked that day are April's extra hours.</summary>
    [Fact]
    public void Real_days_of_the_next_month_count_in_the_shared_week()
    {
        var month = new YearMonth(2026, 4);
        var terms = TermsOf(month);
        var may = MonthAssembler.Prepared(TermsOf(new YearMonth(2026, 5)))
            .Select(d => d.Date.Day == 1 ? d with { Present = true, Arrival = Arrival, Departure = Departure.PlusHours(2), Absence = null } : d).ToList();
        var days = MonthAssembler.Prepared(terms);

        var without = MonthAssembler.Evaluate(terms, days, [], [], null, 0m, 0m).Figures!;
        var with = MonthAssembler.Evaluate(terms, days, [], [], null, 0m, 0m, next: new Statement(Item, month.PlusMonths(1), false, 0m, null, [may[0]], null, 1)).Figures!;

        Assert.Equal(2m, with.Declaration.ComplementaryHours - without.Declaration.ComplementaryHours);
    }

    /// <summary>Decision 30.</summary>
    [Fact]
    public void Declared_amounts_replace_the_computed_ones_and_the_parts_still_add_up()
    {
        var terms = TermsOf(new YearMonth(2025, 9));
        var days = MonthAssembler.Prepared(terms);
        var computed = MonthAssembler.Evaluate(terms, days, [], [], null, 0m, 10m).Figures!;
        var figures = MonthAssembler.Evaluate(terms, days, [], [], null, 0m, 10m, new DeclaredFigures(600m, 50m, 40m)).Figures!;

        Assert.Equal(600m, figures.Declaration.NetPay);
        Assert.Equal(50m, figures.Declaration.Upkeep);
        Assert.Equal(40m, figures.Declaration.Meals);
        Assert.Equal(680m, figures.Cost);
        Assert.Equal(640m, figures.TaxBase);
        Assert.Equal(600m, figures.BasePay - figures.Deduction + figures.ExtraPay + figures.PaidLeavePay);
        Assert.Equal(computed.BasePay, figures.BasePay);

        var onlyMeals = MonthAssembler.Evaluate(terms, days, [], [], null, 0m, 10m, new DeclaredFigures(null, null, 40m)).Figures!;
        Assert.Equal(computed.Declaration.NetPay, onlyMeals.Declaration.NetPay);
        Assert.Equal(computed.Declaration.Upkeep, onlyMeals.Declaration.Upkeep);
    }

    /// <summary>Decision 30: entered hours are paid, and the other fields replace the month's as they are.</summary>
    [Fact]
    public void Declared_hours_and_days_replace_the_computed_ones()
    {
        var terms = TermsOf(new YearMonth(2025, 9));
        var days = MonthAssembler.Prepared(terms);
        var computed = MonthAssembler.Evaluate(terms, days, [], [], null, 0m, 0m).Figures!;
        var figures = MonthAssembler.Evaluate(terms, days, [], [], null, 0m, 0m,
            new DeclaredFigures(NormalHours: 120, ActivityDays: 11, PaidLeaveDays: 3, ComplementaryHours: 2m, OvertimeHours: 1m)).Figures!;

        var declaration = figures.Declaration;
        Assert.Equal((120, 11, 3, 2m, 1m), (declaration.NormalHours, declaration.ActivityDays, declaration.PaidLeaveDays!.Value, declaration.ComplementaryHours, declaration.OvertimeHours));
        Assert.Equal(ExtraHours.Pay(2m, 1m, 4.30m, 0.10m), figures.ExtraPay);
        Assert.Equal(computed.Declaration.NetPay + figures.ExtraPay - computed.ExtraPay, declaration.NetPay);
    }

    [Fact]
    public void June_without_its_reference_months_has_no_figures()
    {
        var terms = TermsOf(new YearMonth(2026, 6));
        var evaluation = MonthAssembler.Evaluate(terms, MonthAssembler.Prepared(terms), [], [], null, 0m, 0m);
        Assert.Null(evaluation.Figures);
        Assert.NotEmpty(evaluation.Missing);
    }

    private static IReadOnlyList<WeekdaySlot> Days(LocalTime tuesdayDeparture)
        => [.. new[] { IsoDayOfWeek.Monday, IsoDayOfWeek.Tuesday, IsoDayOfWeek.Thursday, IsoDayOfWeek.Friday }
            .Select(d => new WeekdaySlot(d, new LocalTime(8, 15), d == IsoDayOfWeek.Tuesday ? tuesdayDeparture : new LocalTime(18, 0), true))];

    /// <summary>The household's two weeks from Monday 2025-09-01: the exception moves the week of 2025-09-08 from Courte to Longue.</summary>
    [Fact]
    public void An_override_changes_the_days_the_month_prepares()
    {
        Terms[] revisions = [new(Item, D(2025, 9, 1), null, 4.40m, null, 0.10m, 3.80m, 3.20m,
            [new WeekType("Longue", 23, Days(new LocalTime(18, 0))), new WeekType("Courte", 22, Days(new LocalTime(16, 45)))])];
        var month = new YearMonth(2025, 9);
        LocalTime? TuesdayDeparture(MonthTerms terms) => MonthAssembler.Prepared(terms).Single(d => d.Date == D(2025, 9, 9)).Departure;

        Assert.Equal(new LocalTime(16, 45), TuesdayDeparture(MonthTerms.For(revisions, [], [], month)));
        var moved = MonthTerms.For(revisions, [], [new WeekOverride(D(2025, 9, 8), 0)], month);
        Assert.Equal(new LocalTime(18, 0), TuesdayDeparture(moved));
        Assert.Equal(0, moved.Plan.TypeOf(D(2025, 9, 9)));
    }
}
