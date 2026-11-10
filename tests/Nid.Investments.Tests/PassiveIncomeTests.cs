using System.Globalization;
using Nid.Core.Contracts;
using Nid.Investments.Application;
using Nid.Investments.Contracts;
using Nid.Investments.Domain;
using Nid.RealEstate.Contracts;
using NodaTime;

namespace Nid.Investments.Tests;

/// <summary>Passive income spec D3–D5, C1, C3–C8: the household's income of the last 12 months on a fixture household.</summary>
public class PassiveIncomeTests
{
    private static readonly LocalDate Today = new(2026, 11, 2);
    private static readonly LocalDate From = new(2025, 12, 1);

    /// <summary>1 USD = 0.8 EUR today.</summary>
    private static readonly RateTable Rates = new([new FxRate(Today, Currency.Eur, 0.8m, FxRateSource.Ecb)], Today, 7);

    private static readonly string[] Window =
        ["2025-12", "2026-01", "2026-02", "2026-03", "2026-04", "2026-05", "2026-06", "2026-07", "2026-08", "2026-09", "2026-10", "2026-11"];

    private static LocalDate D(int y, int m, int d) => new(y, m, d);

    private static PassiveIncome Build(
        IReadOnlyList<EnvelopePerformance.Facts> facts, IReadOnlyList<EnvelopeHolding>? holdings = null, IReadOnlyList<HoldingMovement>? movements = null,
        IReadOnlyList<PropertyIncomeSource>? properties = null, RateTable? rates = null)
        => PassiveIncomeMath.Build(facts, holdings ?? [], movements ?? [], properties ?? [], Currency.Eur, rates ?? Rates, Today);

    private static decimal? N(AmountView? money) => money is null ? null : decimal.Parse(money.Amount, CultureInfo.InvariantCulture);

    /// <summary>The five kinds in their order: dividend, interest, distribution, rent, other.</summary>
    private static decimal?[] Kinds(IReadOnlyList<IncomeKindView> byKind)
    {
        Assert.Equal(PassiveIncomeKinds.All, byKind.Select(k => k.Kind));
        return byKind.Select(k => N(k.Amount)).ToArray();
    }

    private static IncomeMonthView Month(PassiveIncome p, string month) => p.Months.Single(m => m.Month == month);

    /// <summary>The months other than <paramref name="except"/> earn nothing.</summary>
    private static void NothingElse(PassiveIncome p, params string[] except)
        => Assert.All(p.Months.Where(m => !except.Contains(m.Month)), m => Assert.Equal(0m, N(m.Total)));

    private static PropertyIncomeSource Property(
        string name, Func<LocalDate, (decimal Rent, decimal Costs)> month, decimal? value = null, bool archived = false, bool letToday = true)
    {
        var months = Enumerable.Range(0, 12).Select(i => From.PlusMonths(i)).Select(m => new PropertyIncomeMonth(m, month(m).Rent, month(m).Costs)).ToList();
        var values = PassiveIncomeMath.MonthEnds(Today).ToDictionary(d => d, _ => value);
        return new PropertyIncomeSource(ItemId.New(), name, Currency.Eur, archived, months, values, letToday);
    }

    private static EnvelopeHolding Holding(ItemId envelope, string kind)
        => new(Guid.NewGuid(), envelope, kind, null, kind, null, null, null, null, null, null, null, null);

    private static HoldingMovement Movement(EnvelopeHolding holding, LocalDate on, string kind, decimal amount)
        => new(Guid.NewGuid(), holding.Envelope, holding.Id, on, kind, null, null, amount, null, null, null, null);

    [Fact]
    public void The_window_is_the_twelve_months_to_today()
    {
        Assert.Equal(From, PassiveIncomeMath.WindowStart(Today));
        Assert.Equal(
            [D(2025, 12, 31), D(2026, 1, 31), D(2026, 2, 28), D(2026, 3, 31), D(2026, 4, 30), D(2026, 5, 31), D(2026, 6, 30), D(2026, 7, 31),
             D(2026, 8, 31), D(2026, 9, 30), D(2026, 10, 31), Today],
            PassiveIncomeMath.MonthEnds(Today));
    }

    [Fact]
    public void A_dividend_paid_out_counts_and_the_envelope_gain_does_not_move()
    {
        EnvelopeFacts Cto(string? category) => new EnvelopeFacts("CTO", EnvelopeKind.Brokerage)
            .Flow(D(2025, 1, 10), FlowKind.Opening, 10_000m).Value(D(2025, 1, 10), 10_000m)
            .Flow(D(2026, 3, 15), FlowKind.Withdrawal, 120m, category).Value(D(2026, 3, 31), 10_380m);
        var classified = Cto(FlowCategories.Dividend).Build();
        var plain = Cto(null).Build();

        var p = Build([classified]);

        Assert.Equal([120m, 0m, 0m, 0m, 0m], Kinds(Month(p, "2026-03").ByKind));
        NothingElse(p, "2026-03");
        Assert.Equal(120m, N(p.Total));
        var row = Assert.Single(p.Rows);
        Assert.Equal(("envelope", classified.Item.Id, "CTO", "brokerage"), (row.Source, row.Id, row.Name, row.Kind));
        Assert.Equal([120m, 0m, 0m, 0m, 0m], Kinds(row.ByKind));
        Assert.Equal(
            EnvelopeMath.At(plain.Values, plain.Flows, plain.Currency, ValuationCadence.Monthly, null, Today),
            EnvelopeMath.At(classified.Values, classified.Flows, classified.Currency, ValuationCadence.Monthly, null, Today));
        Assert.Empty(Build([plain]).Rows);
    }

    [Fact]
    public void Income_flows_count_by_category_else_as_other()
    {
        var cto = new EnvelopeFacts("CTO", EnvelopeKind.Brokerage)
            .Flow(D(2025, 11, 15), FlowKind.Income, 99m, FlowCategories.Dividend) // before the window
            .Flow(D(2026, 5, 10), FlowKind.Income, 30m, FlowCategories.Interest)
            .Flow(D(2026, 6, 10), FlowKind.Income, 10m).Build();

        var p = Build([cto]);

        Assert.Equal([0m, 30m, 0m, 0m, 10m], Kinds(p.ByKind));
        Assert.Equal(30m, N(Month(p, "2026-05").Total));
        Assert.Equal([0m, 0m, 0m, 0m, 10m], Kinds(Month(p, "2026-06").ByKind));
        NothingElse(p, "2026-05", "2026-06");
        Assert.Equal(40m, N(p.Total));
    }

    [Fact]
    public void Holding_movements_count_as_dividend_on_shares_and_distribution_on_funds()
    {
        var life = new EnvelopeFacts("Assurance-vie", EnvelopeKind.LifeInsurance).Build();
        var (etf, fund, euro) = (Holding(life.Item.Id, "etf"), Holding(life.Item.Id, "fund"), Holding(life.Item.Id, "euro_fund"));
        var movements = new[]
        {
            Movement(etf, D(2026, 4, 10), MovementKinds.Income, 50m),
            Movement(fund, D(2026, 4, 12), MovementKinds.Income, 40m),
            Movement(euro, D(2025, 12, 31), MovementKinds.Interest, 25m),
            Movement(fund, D(2026, 4, 1), MovementKinds.AllocationIn, 1_000m),
        };

        var p = Build([life], [etf, fund, euro], movements);

        Assert.Equal([50m, 0m, 40m, 0m, 0m], Kinds(Month(p, "2026-04").ByKind));
        Assert.Equal([0m, 25m, 0m, 0m, 0m], Kinds(Month(p, "2025-12").ByKind));
        NothingElse(p, "2026-04", "2025-12");
        Assert.Equal(115m, N(Assert.Single(p.Rows).Total));
    }

    [Fact]
    public void A_savings_account_earns_its_monthly_performance()
    {
        // +200 by the end of June; −1 000 withdrawn in August, +60 by the end of September.
        var savings = new EnvelopeFacts("Livret A", EnvelopeKind.Savings)
            .Flow(D(2025, 6, 1), FlowKind.Opening, 10_000m).Value(D(2025, 11, 30), 10_000m)
            .Flow(D(2026, 2, 10), FlowKind.Contribution, 500m).Value(D(2026, 2, 28), 10_500m)
            .Value(D(2026, 6, 30), 10_700m)
            .Flow(D(2026, 8, 5), FlowKind.Withdrawal, 1_000m).Value(D(2026, 9, 30), 9_760m).Build();

        var p = Build([savings]);

        Assert.Equal([0m, 200m, 0m, 0m, 0m], Kinds(Month(p, "2026-06").ByKind));
        Assert.Equal([0m, 60m, 0m, 0m, 0m], Kinds(Month(p, "2026-09").ByKind));
        NothingElse(p, "2026-06", "2026-09");
        var window = EnvelopeMath.Period(savings.Values, savings.Flows, savings.Currency, From.PlusDays(-1), Today).Performance!.Value.Amount;
        Assert.Equal(260m, window);
        Assert.Equal(window, N(p.Total));
        Assert.Equal(("envelope", "savings"), (Assert.Single(p.Rows).Source, p.Rows[0].Kind));
        Assert.Empty(p.Excluded);
    }

    [Fact]
    public void A_savings_account_first_valued_in_a_window_earns_nothing_in_it()
    {
        // Opened with 10 000 in January, first valued at 10 400 in March: the 400 are not March's interest; +50 by the end of April.
        var savings = new EnvelopeFacts("Livret A", EnvelopeKind.Savings)
            .Flow(D(2026, 1, 15), FlowKind.Opening, 10_000m).Value(D(2026, 3, 31), 10_400m).Value(D(2026, 4, 30), 10_450m).Build();

        var p = Build([savings]);

        Assert.Equal(0m, N(Month(p, "2026-03").Total));
        Assert.Equal([0m, 50m, 0m, 0m, 0m], Kinds(Month(p, "2026-04").ByKind));
        Assert.Equal(50m, N(p.Total));
    }

    [Fact]
    public void A_classified_withdrawal_on_a_savings_account_is_counted_once()
    {
        // Written before the rule refusing it: the 300 withdrawn are in the performance already.
        var savings = new EnvelopeFacts("Livret A", EnvelopeKind.Savings)
            .Flow(D(2025, 1, 1), FlowKind.Opening, 10_000m).Value(D(2025, 11, 30), 10_000m)
            .Flow(D(2026, 3, 10), FlowKind.Withdrawal, 300m, FlowCategories.Interest).Value(D(2026, 3, 31), 9_900m).Build();

        var p = Build([savings]);

        Assert.Equal([0m, 200m, 0m, 0m, 0m], Kinds(Month(p, "2026-03").ByKind));
        Assert.Equal(200m, N(p.Total));
    }

    [Fact]
    public void An_income_flow_on_a_savings_account_is_counted_once_as_interest()
    {
        // The valuation holds the 507.25 credited: they are the performance, not an income on top of it.
        var savings = new EnvelopeFacts("Livret A", EnvelopeKind.Savings)
            .Flow(D(2025, 1, 1), FlowKind.Opening, 10_000m).Value(D(2025, 11, 30), 10_000m)
            .Flow(D(2026, 1, 1), FlowKind.Income, 507.25m).Value(D(2026, 1, 1), 10_507.25m).Build();

        var p = Build([savings]);

        Assert.Equal([0m, 507.25m, 0m, 0m, 0m], Kinds(Month(p, "2026-01").ByKind));
        Assert.Equal([0m, 507.25m, 0m, 0m, 0m], Kinds(Assert.Single(p.Rows).ByKind));
        Assert.Equal(507.25m, N(p.Total));
    }

    [Fact]
    public void A_savings_account_s_holding_movements_are_in_its_performance_already()
    {
        var savings = new EnvelopeFacts("Livret A", EnvelopeKind.Savings)
            .Flow(D(2025, 1, 1), FlowKind.Opening, 10_000m).Value(D(2025, 11, 30), 10_000m).Value(D(2026, 6, 30), 10_025m).Build();
        var cash = Holding(savings.Item.Id, "cash");

        var p = Build([savings], [cash], [Movement(cash, D(2026, 6, 30), MovementKinds.Interest, 25m)]);

        Assert.Equal([0m, 25m, 0m, 0m, 0m], Kinds(Month(p, "2026-06").ByKind));
        Assert.Equal(25m, N(p.Total));
    }

    [Fact]
    public void A_savings_account_closed_in_the_window_counts_and_leaves_the_estimate()
    {
        var savings = new EnvelopeFacts("Livret A", EnvelopeKind.Savings) { ClosedOn = D(2026, 5, 15) }
            .Flow(D(2025, 1, 1), FlowKind.Opening, 10_000m).Value(D(2025, 11, 30), 10_000m).Value(D(2026, 3, 31), 10_100m)
            .Flow(D(2026, 5, 15), FlowKind.Withdrawal, 10_100m).Value(D(2026, 5, 15), 0m).Build();
        var cto = new EnvelopeFacts("CTO", EnvelopeKind.Brokerage).Flow(D(2026, 3, 1), FlowKind.Income, 100m, FlowCategories.Dividend).Build();

        var p = Build([savings, cto]);

        Assert.Equal([100m, 100m, 0m, 0m, 0m], Kinds(p.ByKind));
        Assert.Equal(200m, N(p.Total));
        Assert.Equal([100m, 0m, 0m, 0m, 0m], Kinds(p.Estimate.ByKind));
        Assert.Equal(100m, N(p.Estimate.Total));
    }

    [Fact]
    public void A_bank_linked_savings_account_is_left_out_even_with_flows()
    {
        var linked = new EnvelopeFacts("Livret A", EnvelopeKind.Savings)
            .Flow(D(2025, 1, 1), FlowKind.Opening, 10_000m).Value(D(2025, 11, 30), 10_000m)
            .Flow(D(2026, 2, 1), FlowKind.Income, 15m, FlowCategories.Interest)
            .Value(D(2026, 5, 31), 10_150m, FactSource.BankApi).Build();
        // A bank balance before the window leaves the account counted.
        var before = new EnvelopeFacts("LDDS", EnvelopeKind.Savings)
            .Flow(D(2025, 1, 1), FlowKind.Opening, 5_000m).Value(D(2025, 11, 30), 5_000m, FactSource.BankApi)
            .Value(D(2026, 1, 31), 5_100m).Build();

        var p = Build([linked, before]);

        Assert.Equal(new ExcludedEnvelopeView(linked.Item.Id, "Livret A", PassiveIncomeCodes.BankLinkedSavings), Assert.Single(p.Excluded));
        Assert.Equal(before.Item.Id, Assert.Single(p.Rows).Id);
        Assert.Equal(100m, N(p.Total));
        Assert.Equal([before.Item.Id, linked.Item.Id], p.ExposedItems());
    }

    [Fact]
    public void A_savings_account_without_flows_is_left_out()
    {
        var bare = new EnvelopeFacts("Livret A", EnvelopeKind.Savings).Value(D(2025, 11, 30), 10_000m).Value(D(2026, 6, 30), 10_200m).Build();
        // Closed before the window: neither counted nor named.
        var closed = new EnvelopeFacts("Livret jeune", EnvelopeKind.Savings) { ClosedOn = D(2025, 10, 1) }
            .Value(D(2025, 9, 30), 1_000m).Build();

        var p = Build([bare, closed]);

        Assert.Equal(new ExcludedEnvelopeView(bare.Item.Id, "Livret A", PassiveIncomeCodes.SavingsWithoutFlows), Assert.Single(p.Excluded));
        Assert.Empty(p.Rows);
        Assert.Equal(0m, N(p.Total));
    }

    [Fact]
    public void Net_rent_is_rent_minus_costs()
    {
        var flat = Property("Studio", m => m.Month == 10 ? (800m, 1_300m) : (800m, 100m));

        var p = Build([], properties: [flat]);

        Assert.Equal([0m, 0m, 0m, -500m, 0m], Kinds(Month(p, "2026-10").ByKind));
        Assert.All(p.Months.Where(m => m.Month != "2026-10"), m => Assert.Equal(700m, N(m.Total)));
        Assert.Equal(11 * 700m - 500m, N(p.Total));
        var row = Assert.Single(p.Rows);
        Assert.Equal(("property", flat.Item, "Studio", "property"), (row.Source, row.Id, row.Name, row.Kind));
        Assert.Equal([0m, 0m, 0m, 7_200m, 0m], Kinds(row.ByKind));
    }

    [Fact]
    public void A_foreign_amount_converts_at_today_s_rate_and_a_missing_rate_is_named()
    {
        var usd = new EnvelopeFacts("Brokerage US", EnvelopeKind.Brokerage, "USD").Flow(D(2026, 4, 15), FlowKind.Withdrawal, 100.01m, FlowCategories.Dividend).Build();
        var eur = new EnvelopeFacts("CTO", EnvelopeKind.Brokerage).Flow(D(2026, 4, 20), FlowKind.Income, 50m, FlowCategories.Dividend).Build();

        var p = Build([usd, eur]);

        Assert.Equal(80.01m, N(p.Rows[0].Total)); // 100.01 × 0.8 = 80.008
        Assert.Equal(130.01m, N(p.Total));
        Assert.Equal(130.01m, N(Month(p, "2026-04").Total));
        Assert.Empty(p.Findings);

        var missing = Build([usd, eur], rates: RateTable.Empty(Today, 7));

        Assert.Equal(["CTO", "Brokerage US"], missing.Rows.Select(r => r.Name));
        Assert.Null(missing.Rows[1].Total);
        Assert.Equal(50m, N(missing.Rows[0].Total));
        Assert.Null(missing.Total);
        Assert.Null(Month(missing, "2026-04").Total);
        Assert.Equal([null, 0m, 0m, 0m, 0m], Kinds(missing.ByKind));
        Assert.Equal(FxCodes.RateMissing, Assert.Single(missing.Findings).Code);
    }

    [Fact]
    public void The_yield_is_income_over_the_average_value()
    {
        var cto = new EnvelopeFacts("CTO", EnvelopeKind.Brokerage).Value(D(2025, 11, 30), 10_000m)
            .Flow(D(2026, 2, 1), FlowKind.Income, 300m, FlowCategories.Dividend).Build();
        // Valued from May only: 6 000 at its 7 month ends.
        var pea = new EnvelopeFacts("PEA", EnvelopeKind.Pea).Value(D(2026, 5, 31), 6_000m)
            .Flow(D(2026, 7, 1), FlowKind.Income, 120m, FlowCategories.Dividend).Build();
        var flat = Property("Studio", m => m.Month == 10 ? (800m, 1_300m) : (800m, 100m), value: 200_000m);
        var unvalued = new EnvelopeFacts("Compte-titres", EnvelopeKind.Brokerage).Flow(D(2026, 2, 1), FlowKind.Income, 50m, FlowCategories.Dividend).Build();

        var one = Build([cto]);
        Assert.Equal(3.0m, one.Rows[0].YieldPct);
        Assert.Equal(3.0m, one.YieldPct);

        var all = Build([cto, pea], properties: [flat]);
        Assert.Equal([3.6m, 3.0m, 2.0m], all.Rows.Select(r => r.YieldPct));
        Assert.Equal(EnvelopeMath.RoundPercent((7_200m + 300m + 120m) / (200_000m + 10_000m + 6_000m) * 100m), all.YieldPct);

        var none = Build([unvalued]);
        Assert.Null(none.Rows[0].YieldPct);
        Assert.Null(none.YieldPct);

        // An unvalued row stays out of the household's yield.
        var mixed = Build([cto, unvalued]);
        Assert.Equal([3.0m, null], mixed.Rows.Select(r => r.YieldPct));
        Assert.Equal(3.0m, mixed.YieldPct);
    }

    [Fact]
    public void The_estimate_leaves_out_a_closed_envelope_and_an_archived_property()
    {
        var open = new EnvelopeFacts("CTO", EnvelopeKind.Brokerage).Flow(D(2026, 3, 1), FlowKind.Income, 100m, FlowCategories.Dividend).Build();
        var closed = new EnvelopeFacts("PEA", EnvelopeKind.Pea) { ClosedOn = D(2026, 6, 30) }
            .Flow(D(2026, 3, 1), FlowKind.Income, 50m, FlowCategories.Distribution).Build();
        var let = Property("Studio", m => m.Month == 1 ? (1_000m, 100m) : (0m, 0m));
        var archived = Property("Maison", m => m.Month == 2 ? (400m, 0m) : (0m, 0m), archived: true, letToday: false);
        var ended = Property("Garage", m => m.Month == 3 ? (300m, 0m) : (0m, 0m), letToday: false);

        var p = Build([open, closed], properties: [let, archived, ended]);

        Assert.Equal(100m + 50m + 900m + 400m + 300m, N(p.Total));
        Assert.Equal(1_000m, N(p.Estimate.Total));
        Assert.Equal([100m, 0m, 0m, 900m, 0m], Kinds(p.Estimate.ByKind));
        Assert.Equal([100m, 0m, 50m, 1_600m, 0m], Kinds(p.ByKind));
    }

    [Fact]
    public void Months_add_up_to_the_total_and_the_current_month_is_partial()
    {
        var cto = new EnvelopeFacts("CTO", EnvelopeKind.Brokerage)
            .Flow(D(2026, 3, 15), FlowKind.Withdrawal, 120m, FlowCategories.Dividend)
            .Flow(D(2026, 11, 1), FlowKind.Income, 7.5m)
            .Flow(D(2026, 11, 3), FlowKind.Income, 1_000m).Build(); // after today
        var usd = new EnvelopeFacts("Brokerage US", EnvelopeKind.Brokerage, "USD").Flow(D(2026, 1, 5), FlowKind.Income, 33.33m, FlowCategories.Interest).Build();
        var flat = Property("Studio", m => m.Month == 10 ? (800m, 1_300m) : (800m, 100m));

        var p = Build([cto, usd], properties: [flat]);

        Assert.Equal((From, Today), (p.From, p.To));
        Assert.Equal("EUR", p.Currency);
        Assert.Equal(Window, p.Months.Select(m => m.Month));
        Assert.Equal([.. Enumerable.Repeat(false, 11), true], p.Months.Select(m => m.Partial));
        Assert.All(p.Months, m => Assert.Equal(N(m.Total), Kinds(m.ByKind).Sum()));
        var total = 7_200m + 120m + 7.5m + 26.66m; // 33.33 × 0.8
        Assert.Equal(total, N(p.Total));
        Assert.Equal(total, p.Months.Sum(m => N(m.Total)));
        Assert.Equal(total, Kinds(p.ByKind).Sum());
        Assert.Equal(total, p.Rows.Sum(r => N(r.Total)));
        Assert.Equal(["Studio", "CTO", "Brokerage US"], p.Rows.Select(r => r.Name));
        Assert.Equal(total, N(p.Estimate.Total));
    }
}
