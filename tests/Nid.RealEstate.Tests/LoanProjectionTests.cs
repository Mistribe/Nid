using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;

namespace Nid.RealEstate.Tests;

/// <summary>Projection spec §4.2–§4.3: the loans' months after an early repayment and a sale, with the real engine.</summary>
public class LoanProjectionTests
{
    private static readonly LocalDate Today = new(2026, 10, 21);
    private static readonly RateTable Rates = new([new FxRate(Today, Currency.Eur, 0.9m, FxRateSource.Ecb)], Today, 7);
    private static readonly ItemId Property = ItemId.New();

    /// <summary>The golden loan (3.5 % over 240 payments), on <see cref="Property"/>.</summary>
    private static OverviewLoan Home()
    {
        var item = new ItemAccess(ItemId.New(), "Maison", Visibility.Shared, [new Owner(PersonId.New(), PersonKind.Adult, 10_000)]) { Currency = "EUR" };
        return new OverviewLoan(item, Loans.Stored(terms: Loans.Golden()) with { PropertyId = Property }, null);
    }

    private static ProjectedLoans Project(OverviewLoan loan, IReadOnlyList<LoanRepaymentEvent> repayments, IReadOnlyList<LoanSaleEvent> sales)
        => LoanProjection.ProjectWithEvents([loan], new HashSet<Currency>(), Currency.Eur, Rates, Today, 12, repayments, sales);

    [Fact]
    public void Without_events_the_months_follow_the_schedule()
    {
        var home = Home();
        var projected = Project(home, [], []);

        Assert.Equal(13, projected.Months.Count);
        Assert.True(projected.Months[0].Debt > projected.Months[12].Debt);
        Assert.All(projected.Months, m => Assert.True(m.Payments > 0m));
        Assert.Equal(home.Item.Id, Assert.Single(projected.Ends).Loan);
        Assert.Empty(projected.Repayments);
    }

    [Fact]
    public void A_repayment_lowers_the_debt_and_a_sale_settles_the_loan_from_its_month()
    {
        var home = Home();
        var repayment = new LoanRepaymentEvent(Guid.NewGuid(), 1, Today.PlusMonths(1), 1_000m, SplitRule.HighestRate, null, RepaymentEffect.ReduceTerm);
        var sale = new LoanSaleEvent(Guid.NewGuid(), 6, Today.PlusMonths(6), Property);
        var plain = Project(home, [], []);

        var projected = Project(home, [repayment], [sale]);

        Assert.Equal(1_000m, Assert.Single(projected.Repayments).Applied);
        Assert.True(projected.Months[2].Debt < plain.Months[2].Debt);
        Assert.All(projected.Months.Skip(6), m => Assert.Equal(0m, m.Debt));
        Assert.All(projected.Months.Skip(7), m => Assert.Equal(0m, m.Payments));
        Assert.True(projected.Settled[sale.Event].Balance > 0m);
        Assert.Equal(sale.On, Assert.Single(projected.Ends).On);
    }

    [Fact]
    public void A_chosen_loan_not_running_takes_nothing()
    {
        var repayment = new LoanRepaymentEvent(Guid.NewGuid(), 1, Today.PlusMonths(1), 1_000m, SplitRule.Chosen, ItemId.New(), RepaymentEffect.ReduceTerm);

        Assert.Equal(0m, Assert.Single(Project(Home(), [repayment], []).Repayments).Applied);
    }

    [Fact]
    public void A_loan_without_a_rate_keeps_its_end_but_leaves_the_figures()
    {
        var item = new ItemAccess(ItemId.New(), "Studio", Visibility.Shared, [new Owner(PersonId.New(), PersonKind.Adult, 10_000)]) { Currency = "USD" };
        var dollars = new OverviewLoan(item, Loans.Stored(terms: Loans.Annuity(30_000m, 0.04m, 120) with { Currency = Currency.Usd }), null);

        var projected = LoanProjection.ProjectWithEvents([dollars], new HashSet<Currency> { Currency.Usd }, Currency.Eur, Rates, Today, 3, [], []);

        Assert.All(projected.Months, m => Assert.Equal((0m, 0m), (m.Debt, m.Payments)));
        Assert.Single(projected.Ends);
    }
}
