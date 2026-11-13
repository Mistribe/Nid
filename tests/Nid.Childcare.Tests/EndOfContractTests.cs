using Nid.Childcare.Application;
using Nid.Childcare.Contracts;
using Nid.Childcare.Domain;
using Nid.Core.Contracts;
using NodaTime;
using static Nid.Childcare.Tests.Fixture;

namespace Nid.Childcare.Tests;

/// <summary>IDCC 3239 art. 120–121: notice by seniority and the breach indemnity.</summary>
public class EndOfContractTests
{
    private static readonly LocalDate Start = D(2026, 1, 5);

    [Theory]
    [InlineData(2026, 3, 4, 2026, 3, 11)] // 1 month of care: 8 calendar days, the notification's included
    [InlineData(2026, 4, 5, 2026, 4, 19)] // 3 months: 15 days
    [InlineData(2026, 12, 31, 2027, 1, 14)] // 11 months: still 15 days
    [InlineData(2027, 1, 5, 2027, 2, 4)] // a year: a month
    public void Notice_follows_seniority(int y, int m, int d, int ey, int em, int ed)
        => Assert.Equal(D(ey, em, ed), EndOfContract.NoticeEnd(EndReason.Withdrawal, Start, D(y, m, d)));

    [Theory]
    [InlineData(EndReason.Misconduct)]
    [InlineData(EndReason.Approval)]
    [InlineData(EndReason.Trial)]
    public void No_notice_ends_on_the_notification(EndReason reason) => Assert.Equal(D(2027, 3, 1), EndOfContract.NoticeEnd(reason, Start, D(2027, 3, 1)));

    [Fact]
    public void A_resignation_gives_the_same_notice() => Assert.Equal(D(2027, 3, 31), EndOfContract.NoticeEnd(EndReason.Resignation, Start, D(2027, 3, 1)));

    [Theory]
    [InlineData(EndReason.Withdrawal, 2026, 10, 4, false)] // 8 months and 29 days
    [InlineData(EndReason.Withdrawal, 2026, 10, 5, true)] // 9 months
    [InlineData(EndReason.Misconduct, 2028, 1, 5, false)]
    [InlineData(EndReason.Approval, 2028, 1, 5, false)]
    [InlineData(EndReason.Resignation, 2028, 1, 5, false)]
    [InlineData(EndReason.ChildDeath, 2026, 2, 5, true)] // whatever the seniority
    public void Indemnity_due(EndReason reason, int y, int m, int d, bool due) => Assert.Equal(due, EndOfContract.IndemnityDue(reason, Start, D(y, m, d)));

    [Fact]
    public void Indemnity_is_an_eightieth_of_the_gross() => Assert.Equal(128.00m, EndOfContract.RuptureIndemnity(8000m, 1.28m));

    [Fact]
    public void Hours_due_keep_paid_absences_and_drop_deducted_ones()
    {
        var november = StatementPreparation.Prepare(new YearMonth(2026, 11), Plan, Gaps);
        StatementDay[] Absent(AbsenceReason reason) => [.. november.Select(d => d.Date.Day == 3 ? d with { Present = false, Arrival = null, Departure = null, Lunch = false, Absence = reason } : d)];

        Assert.Equal(161.5m, EndOfContract.HoursDue(november, Plan, 0, null)); // 17 days × 9.5 h
        Assert.Equal(161.5m, EndOfContract.HoursDue(Absent(AbsenceReason.Family), Plan, 0, null));
        Assert.Equal(152m, EndOfContract.HoursDue(Absent(AbsenceReason.CarerAbsent), Plan, 0, null));
    }
}

/// <summary>The last month of an ended contract (art. 121, 124, 125), every earlier month validated as the template plans it.</summary>
public class LastMonthTests
{
    private static readonly ItemId Item = new(Guid.NewGuid());
    private static readonly LocalDate Start = D(2025, 9, 1);

    private static (List<Statement> Validated, MonthEvaluation Last) Run(LocalDate last, ContractEnd how, IReadOnlyList<CareGap> gaps, decimal? gross = 5.50m)
    {
        Terms[] revisions = [new(Item, Start, last, 4.30m, gross, 0.10m, 3.80m, 3.20m, [new WeekType("Semaine type", ProgrammedWeeks, Schedule)])];
        var validated = new List<Statement>();
        for (var m = Start.ToYearMonth(); m.CompareTo(last.ToYearMonth()) < 0; m = m.PlusMonths(1))
        {
            var terms = MonthTerms.For(revisions, gaps, [], m);
            var days = MonthAssembler.Prepared(terms);
            var figures = MonthAssembler.Evaluate(terms, days, [], validated, validated.LastOrDefault(), 0m, 0m, end: how).Figures!;
            Assert.Equal(0m, figures.Regularisation);
            validated.Add(new Statement(Item, m, true, 0m, 0m, days, figures, 1));
        }
        var final = MonthTerms.For(revisions, gaps, [], last.ToYearMonth());
        return (validated, MonthAssembler.Evaluate(final, MonthAssembler.Prepared(final), [], validated, validated[^1], 0m, 0m, end: how));
    }

    [Fact]
    public void Welcomed_every_week_the_carer_is_owed_the_balance()
    {
        var (_, last) = Run(D(2025, 12, 31), new ContractEnd(EndReason.Withdrawal, D(2025, 12, 1)), []);
        var f = last.Figures!;
        Assert.True(f.Regularisation > 0m);
        Assert.Equal(f.BasePay - f.Deduction + f.ExtraPay + f.Regularisation, f.Declaration.NetPay);
        Assert.Equal(0m, f.Declaration.RuptureIndemnity); // 3 months of care
    }

    [Fact]
    public void Weeks_off_taken_early_leave_a_balance_owed_to_the_family()
    {
        var (_, last) = Run(D(2025, 12, 31), new ContractEnd(EndReason.Resignation, D(2025, 12, 1)), [new CareGap(D(2025, 9, 8), 6)]);
        Assert.True(last.Figures!.Regularisation < 0m);
    }

    [Fact]
    public void Leave_since_june_and_the_indemnity_are_settled()
    {
        var (validated, last) = Run(D(2027, 3, 31), new ContractEnd(EndReason.Withdrawal, D(2027, 2, 26)), []);
        var f = last.Figures!;
        Assert.Equal(25, f.Declaration.PaidLeaveDays); // June 2026 to March 2027: 10 months × 2.5
        Assert.True(f.Declaration.LeaveIndemnity > 0m);
        var net = validated.Sum(s => s.Figures!.Declaration.NetPay) + f.Declaration.NetPay + f.Declaration.LeaveIndemnity;
        Assert.Equal(Mensualisation.Round(net * (5.50m / 4.30m) / 80m), f.Declaration.RuptureIndemnity);
        Assert.Equal(f.Declaration.NetPay + f.Declaration.LeaveIndemnity + f.Declaration.RuptureIndemnity + f.Declaration.Upkeep + f.Declaration.Meals, f.Cost);
        // Not a salary: the indemnity stays out of the childcare tax base.
        Assert.Equal(f.Declaration.NetPay + f.Declaration.LeaveIndemnity + f.Declaration.Upkeep, f.TaxBase);
    }

    [Fact]
    public void The_indemnity_needs_the_gross_rate()
    {
        var (_, last) = Run(D(2026, 7, 31), new ContractEnd(EndReason.Withdrawal, D(2026, 7, 1)), [], gross: null);
        Assert.Null(last.Figures);
        Assert.Contains(("terms.grossHourlyRate", ChildcareCodes.GrossRateRequired), last.Problems);
    }

    [Fact]
    public void Every_earlier_month_is_validated_first()
    {
        var (validated, _) = Run(D(2025, 12, 31), new ContractEnd(EndReason.Withdrawal, D(2025, 12, 1)), []);
        Terms[] revisions = [new(Item, Start, D(2025, 12, 31), 4.30m, 5.50m, 0.10m, 3.80m, 3.20m, [new WeekType("Semaine type", ProgrammedWeeks, Schedule)])];
        var final = MonthTerms.For(revisions, [], [], new YearMonth(2025, 12));
        var last = MonthAssembler.Evaluate(final, MonthAssembler.Prepared(final), [], validated.Where(s => s.Month != new YearMonth(2025, 10)).ToList(), validated[^1], 0m, 0m,
            end: new ContractEnd(EndReason.Withdrawal, D(2025, 12, 1)));
        Assert.Equal([new YearMonth(2025, 10)], last.Missing);
    }
}
