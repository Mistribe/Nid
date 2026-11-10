using System.Globalization;
using Nid.Core.Application.Reports;
using Nid.Core.Contracts;
using Nid.Core.Domain.Tax;
using NodaTime;

namespace Nid.Core.Tests;

/// <summary>Issue #12: the year's net worth change split into its parts, each date at its own rate.</summary>
public class YearReviewMathTests
{
    private static readonly LocalDate Start = new(2023, 12, 31);
    private static readonly LocalDate End = new(2024, 12, 31);

    private static Dictionary<Currency, decimal> Eur(decimal amount) => new() { [Currency.Eur] = amount };

    private static decimal D(string amount) => decimal.Parse(amount, CultureInfo.InvariantCulture);

    [Fact]
    public void The_parts_add_up_to_the_change()
    {
        // Investments: 1 000 paid in, 300 of gain. Real estate: a 250 000 home bought with a 200 000 loan, 2 000 of capital repaid,
        // and its estimate 5 000 down.
        var split = YearReviewMath.Split(
            [
                ("investments", new NetWorthPoint(Start, Eur(10_000m), Eur(8_000m), 1), new NetWorthPoint(End, Eur(11_300m), Eur(9_000m), 1)),
                ("real_estate", new NetWorthPoint(Start, Eur(0m), Eur(0m), 0, Eur(0m)),
                    new NetWorthPoint(End, Eur(245_000m - 198_000m), Eur(2_000m), 2, Eur(50_000m))),
            ],
            Currency.Eur, RateTable.Empty(End, 7), Start, End, out _)!;

        Assert.Equal(("10000.00", "58300.00", "48300.00"), (split.Start.Amount, split.End.Amount, split.Change.Amount));
        Assert.Equal(
            [("real_estate.moved", "50000.00"), ("real_estate.value", "-5000.00"), ("real_estate.paid_in", "2000.00"),
                ("investments.paid_in", "1000.00"), ("investments.value", "300.00")],
            split.Parts.Select(p => (p.Key, p.Amount.Amount)));
        Assert.Equal(D(split.Change.Amount), split.Parts.Sum(p => D(p.Amount.Amount)));
        // The bars run from the start: 10 000 → 60 000 → 55 000 → 57 000 → 58 000 → 58 300.
        Assert.Equal(
            [("10000.00", "60000.00"), ("55000.00", "60000.00"), ("55000.00", "57000.00"), ("57000.00", "58000.00"), ("58000.00", "58300.00")],
            split.Parts.Select(p => (p.Low.Amount, p.High.Amount)));
    }

    [Fact]
    public void A_foreign_part_converts_at_each_date()
    {
        var rates = new RateTable(
            [new FxRate(Start, Currency.Eur, 0.8m, FxRateSource.Ecb), new FxRate(End, Currency.Eur, 0.9m, FxRateSource.Ecb)], End, maxGapDays: 7);
        var usd = (decimal amount) => new Dictionary<Currency, decimal> { [Currency.Usd] = amount };

        var split = YearReviewMath.Split(
            [("investments", new NetWorthPoint(Start, usd(1_000m), usd(500m), 1), new NetWorthPoint(End, usd(1_200m), usd(600m), 1))],
            Currency.Eur, rates, Start, End, out _)!;

        // 1 200 × 0.9 − 1 000 × 0.8.
        Assert.Equal(("800.00", "1080.00", "280.00"), (split.Start.Amount, split.End.Amount, split.Change.Amount));
        Assert.Equal(D(split.Change.Amount), split.Parts.Sum(p => D(p.Amount.Amount)));
    }

    [Fact]
    public void Nothing_before_and_after_is_no_section()
        => Assert.Null(YearReviewMath.Split(
            [("investments", new NetWorthPoint(Start, Eur(0m), Eur(0m), 0), new NetWorthPoint(End, Eur(0m), Eur(0m), 0))],
            Currency.Eur, RateTable.Empty(End, 7), Start, End, out _));

    [Fact]
    public void Converted_parts_add_up_to_the_change_to_the_cent()
    {
        // At 0.333 each part has fractions of a cent: rounded one by one they would sum to 411.12, the rounded ends differ by 411.13.
        var rates = new RateTable(
            [new FxRate(Start, Currency.Eur, 0.333m, FxRateSource.Ecb), new FxRate(End, Currency.Eur, 0.333m, FxRateSource.Ecb)], End, maxGapDays: 7);
        var usd = (decimal amount) => new Dictionary<Currency, decimal> { [Currency.Usd] = amount };

        var split = YearReviewMath.Split(
            [
                ("investments", new NetWorthPoint(Start, usd(1_000.01m), usd(500.02m), 1), new NetWorthPoint(End, usd(1_234.56m), usd(700.07m), 1)),
                ("real_estate", new NetWorthPoint(Start, usd(1_000m), usd(100m), 1, usd(0m)),
                    new NetWorthPoint(End, usd(2_000.05m), usd(300.03m), 2, usd(1_500.01m))),
            ],
            Currency.Eur, rates, Start, End, out _)!;

        Assert.Equal(("666.00", "1077.13", "411.13"), (split.Start.Amount, split.End.Amount, split.Change.Amount));
        Assert.Equal(5, split.Parts.Count);
        Assert.Equal(D(split.Change.Amount), split.Parts.Sum(p => D(p.Amount.Amount)));
        Assert.Equal(D(split.End.Amount) - D(split.Start.Amount), D(split.Change.Amount));
        Assert.Equal((split.Start.Amount, split.End.Amount), (split.Parts[0].Low.Amount, split.Parts[^1].High.Amount));
    }

    [Fact]
    public void A_missing_rate_is_no_section_and_names_its_currency()
    {
        var split = YearReviewMath.Split(
            [("investments", new NetWorthPoint(Start, new Dictionary<Currency, decimal> { [Currency.Usd] = 10m }, Eur(0m), 1),
                new NetWorthPoint(End, Eur(10m), Eur(0m), 1))],
            Currency.Eur, RateTable.Empty(End, 7), Start, End, out var missing);

        Assert.Equal((null, "USD"), (split, missing));
    }

    private static Dictionary<Currency, decimal> Usd(decimal amount) => new() { [Currency.Usd] = amount };

    private static CommitmentsPart Cash(string source, Dictionary<Currency, decimal> cash, bool estimated = false)
        => new(source, 1, cash, cash, estimated);

    /// <summary>The months of 2025 to 15 October: each read on its last day, October on the 15th.</summary>
    private static List<(LocalDate On, IReadOnlyList<CommitmentsPart> Parts)> Months(int count, Func<int, IReadOnlyList<CommitmentsPart>> parts)
        => Enumerable.Range(1, count)
            .Select(m => (m == 10 ? new LocalDate(2025, 10, 15) : new LocalDate(2025, m, 1).PlusMonths(1).PlusDays(-1), parts(m))).ToList();

    [Fact]
    public void Engagements_sum_the_months_cash_at_each_months_rate()
    {
        // 100 $ a month: 0.8 € per $ on 31 January, 0.9 € on 28 February. Childcare not yet validated in February.
        var rates = new RateTable(
            [new FxRate(new LocalDate(2025, 1, 31), Currency.Eur, 0.8m, FxRateSource.Ecb), new FxRate(new LocalDate(2025, 2, 28), Currency.Eur, 0.9m, FxRateSource.Ecb)],
            new LocalDate(2025, 2, 28), maxGapDays: 7);

        var sum = YearReviewMath.Engagements(
            Months(2, m => [Cash(CommitmentSources.Loans, Eur(500m)), Cash(CommitmentSources.Subscriptions, Usd(100m)),
                Cash(CommitmentSources.Childcare, Eur(300m), estimated: m == 2)]),
            0m, Currency.Eur, rates);

        Assert.Equal(
            [(CommitmentSources.Subscriptions, (decimal?)170m, false), (CommitmentSources.Loans, 1_000m, false),
                (CommitmentSources.Childcare, 600m, true)],
            sum.Sources.Select(s => (s.Source, s.Amount, s.Estimated)));
        Assert.Equal(1_770m, sum.Total);
        Assert.Null(sum.Missing);
    }

    [Fact]
    public void Tax_is_the_estimate_pro_rata()
    {
        var sum = YearReviewMath.Engagements(Months(10, _ => [Cash(CommitmentSources.Loans, Eur(100m))]), 2_400m, Currency.Eur, RateTable.Empty(End, 7));

        Assert.Equal([(CommitmentSources.Loans, (decimal?)1_000m, false), (CommitmentSources.Tax, 2_000m, true)],
            sum.Sources.Select(s => (s.Source, s.Amount, s.Estimated)));
        Assert.Equal(3_000m, sum.Total);
        // A refund commits nothing.
        Assert.DoesNotContain(YearReviewMath.Engagements(Months(10, _ => []), -500m, Currency.Eur, RateTable.Empty(End, 7)).Sources,
            s => s.Source == CommitmentSources.Tax);
    }

    private static IncomeReviewPart Earned(decimal? total)
        => new(total, [new IncomeReviewSource(new ItemId(Guid.NewGuid()), "salary", "Salaire", "salary", total)], total is null ? ["USD"] : []);

    [Fact]
    public void The_margin_is_income_minus_engagements()
    {
        var engagements = YearReviewMath.Engagements(Months(10, _ => [Cash(CommitmentSources.Loans, Eur(100m))]), 0m, Currency.Eur, RateTable.Empty(End, 7));
        var before = YearReviewMath.Engagements(Months(10, _ => [Cash(CommitmentSources.Loans, Eur(50m))]), 0m, Currency.Eur, RateTable.Empty(End, 7));

        var view = YearReviewMath.Income(Earned(24_000m), engagements, Earned(20_000m), before, Currency.Eur)!;

        Assert.Equal(("24000.00", "1000.00", "23000.00"), (view.Income!.Amount, view.Engagements!.Amount, view.Margin!.Amount));
        Assert.Equal(("20000.00", "500.00", "19500.00"), (view.PreviousIncome!.Amount, view.PreviousEngagements!.Amount, view.PreviousMargin!.Amount));
        Assert.Equal([("salary", "24000.00")], view.Sources.Select(s => (s.Key, s.Amount!.Amount)));
        Assert.Equal([(CommitmentSources.Loans, "1000.00", false)], view.EngagementSources.Select(s => (s.Source, s.Amount!.Amount, s.Estimated)));
        // Nothing earned nor committed is no section.
        var none = YearReviewMath.Engagements(Months(10, _ => []), 0m, Currency.Eur, RateTable.Empty(End, 7));
        Assert.Null(YearReviewMath.Income(new IncomeReviewPart(0m, [], []), none, null, null, Currency.Eur));
        // A loan counted before its first payment commits nothing: no row, no section.
        var notYet = YearReviewMath.Engagements(Months(10, _ => [Cash(CommitmentSources.Loans, Eur(0m))]), 0m, Currency.Eur, RateTable.Empty(End, 7));
        Assert.Empty(notYet.Sources);
        Assert.Null(YearReviewMath.Income(new IncomeReviewPart(0m, [], []), notYet, null, null, Currency.Eur));
        Assert.Null(YearReviewMath.Income(Earned(24_000m), engagements, new IncomeReviewPart(0m, [], []), notYet, Currency.Eur)!.PreviousEngagements);
    }

    [Fact]
    public void A_missing_rate_nulls_the_margin_and_is_a_finding()
    {
        var engagements = YearReviewMath.Engagements(Months(1, _ => [Cash(CommitmentSources.Subscriptions, Usd(10m))]), 0m, Currency.Eur, RateTable.Empty(End, 7));

        var view = YearReviewMath.Income(Earned(24_000m), engagements, null, null, Currency.Eur)!;

        Assert.Equal(("USD", (decimal?)null), (engagements.Missing, engagements.Total));
        Assert.Null(Assert.Single(view.EngagementSources).Amount);
        Assert.Equal((null, null), (view.Engagements, view.Margin));
        Assert.Null(YearReviewMath.Income(Earned(null), engagements, null, null, Currency.Eur)!.Margin);
    }

    [Fact]
    public void The_years_start_at_the_first_holding_or_income()
    {
        LocalDate[] dates = [new(2023, 12, 31), new(2024, 12, 31), new(2025, 10, 15)];
        IReadOnlyList<NetWorthPoint> Held(int from) => dates.Select((d, i) => new NetWorthPoint(d, Eur(0m), Eur(0m), i >= from ? 1 : 0)).ToList();

        Assert.Equal([2024, 2025], YearReviewMath.Years(dates, [Held(1)], 2025));
        Assert.Equal([2022, 2023, 2024, 2025], YearReviewMath.Years(dates, [Held(1)], 2025, new LocalDate(2022, 3, 1)));
        Assert.Equal([2024, 2025], YearReviewMath.Years(dates, [Held(1)], 2025, new LocalDate(2025, 1, 1)));
        // No further back than the review reaches.
        Assert.Equal(2025 - YearReviewMath.MaxYears, YearReviewMath.Years(dates, [Held(3)], 2025, new LocalDate(1900, 1, 1))[0]);
    }

    [Fact]
    public void A_section_without_anything_is_null_and_so_is_its_previous_figure()
    {
        var id = new ItemId(Guid.NewGuid());
        SubscriptionsReviewPart noCharge = new(0m, [], [], [], []);
        SubscriptionsReviewPart unrated = new(null, [], [], [], ["USD"]); // a charge without a rate is still a charge
        LoansReviewPart noLoan = new(0m, 0m, 0m, 0m, 0m, 0m, [], []);
        LoansReviewPart loan = new(100.004m, 30m, 0m, 1_000m, 5_000m, 4_000m,
            [new LoanReviewRow(id, "Prêt", 100.004m, 30m, 0m, 1_000m, 5_000m, 4_000m, null)], []);
        PropertiesReviewPart noProperty = new(0m, 0m, [], []);

        Assert.Null(YearReviewMath.Subscriptions(noCharge, unrated, Currency.Eur));
        var subscriptions = YearReviewMath.Subscriptions(unrated, noCharge, Currency.Eur)!;
        Assert.Equal((null, null), (subscriptions.Cost, subscriptions.PreviousCost));
        Assert.Null(YearReviewMath.Loans(noLoan, loan, Currency.Eur));
        Assert.Null(YearReviewMath.Loans(loan, noLoan, Currency.Eur)!.PreviousInterestAndInsurance);
        // The previous cost of credit is the sum of the figures shown, each to the cent.
        Assert.Equal("130.00", YearReviewMath.Loans(loan, loan, Currency.Eur)!.PreviousInterestAndInsurance!.Amount);
        Assert.Null(YearReviewMath.Properties(noProperty, noProperty, Currency.Eur));
    }

    private static TaxResult Estimate(int year, decimal salary, bool hasData = true) => TaxCalculator.Compute(new TaxInputs(
        year, [new("A", 0m, EmploymentStatus.NonExecutive, 0m, 0m, SalaryTaxable: salary)], 0, 0, 0m, 0m, 0m, null, 0m, 0m, 0m,
        0m, 0m, 0m, 0m)) with { HasData = hasData };

    [Fact]
    public void The_tax_section_converts_at_the_end_and_needs_data()
    {
        var now = Estimate(2025, 50_000m);
        Assert.True(now.TotalTax > 0m);
        // 0.8 € per $ on the end: a household in dollars reads each euro as 1.25 $.
        var rates = new RateTable([new FxRate(End, Currency.Eur, 0.8m, FxRateSource.Ecb)], End, maxGapDays: 7);

        static decimal InDollars(decimal eur) => Math.Round(eur * 1.25m, 2, MidpointRounding.AwayFromZero);
        var tax = YearReviewMath.Tax(now, Estimate(2025, 40_000m), Currency.Usd, rates, End, out var missing)!;

        Assert.Null(missing);
        Assert.Equal((InDollars(now.TotalTax), InDollars(now.IncomeTax), InDollars(now.SocialLevies)),
            (D(tax.TotalTax!.Amount), D(tax.IncomeTax!.Amount), D(tax.SocialLevies!.Amount)));
        Assert.Equal("USD", tax.TotalTax.Currency);
        Assert.NotNull(tax.PreviousTotalTax);
        Assert.Null(YearReviewMath.Tax(now, Estimate(2025, 40_000m, hasData: false), Currency.Eur, rates, End, out _)!.PreviousTotalTax);
        Assert.Null(YearReviewMath.Tax(now, null, Currency.Eur, rates, End, out _)!.PreviousTotalTax);
        Assert.Null(YearReviewMath.Tax(Estimate(2025, 0m, hasData: false), null, Currency.Eur, rates, End, out _));
        Assert.Null(YearReviewMath.Tax(null, null, Currency.Eur, rates, End, out _));

        var unconverted = YearReviewMath.Tax(now, null, Currency.Usd, RateTable.Empty(End, 7), End, out missing)!;
        Assert.Equal((null, "EUR"), (unconverted.TotalTax, missing));
    }
}
