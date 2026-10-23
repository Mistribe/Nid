using Nid.Core.Contracts;
using Nid.Core.Domain.Scenarios;
using NodaTime;

namespace Nid.Core.Tests;

/// <summary>Property purchase scenario spec §3: limits and balance of the document.</summary>
public class ScenarioRulesTests
{
    private static readonly LocalDate Today = new(2026, 10, 22);

    private static ScenarioLoan Loan(int months = 240, ScenarioDeferral? deferral = null, decimal amount = 200_000m) =>
        new("Main", amount, 0.036m, months, deferral, new(ScenarioInsuranceKind.RateOnInitial, 0.003m), 0m, 0m);

    private static PropertyPurchase Doc() => new(
        1, PurchaseUse.Primary, new(2027, 3, 1), 300_000m, 22_000m, 0m, 0m, 0m, 900m, 0m, 0, null,
        [], [Loan()], new(10, new Dictionary<string, decimal>(), null, 500m, AllocationClasses.Cash));

    private static string[] Fields(PropertyPurchase doc) => ScenarioRules.Check(doc, Today).Select(e => e.Field).ToArray();

    [Fact]
    public void A_valid_primary_purchase_has_no_refusal() => Assert.Empty(ScenarioRules.Check(Doc(), Today));

    [Fact]
    public void Each_limit_is_refused_at_its_field()
    {
        var doc = Doc();
        var a = doc.Assumptions;
        Assert.Contains("assumptions.horizonYears", Fields(doc with { Assumptions = a with { HorizonYears = 0 } }));
        Assert.Contains("assumptions.horizonYears", Fields(doc with { Assumptions = a with { HorizonYears = 31 } }));
        Assert.Contains("loans", Fields(doc with { Loans = [Loan(), Loan(), Loan(), Loan(), Loan()] }));
        Assert.Contains("loans[0].months", Fields(doc with { Loans = [Loan(11)] }));
        Assert.Contains("loans[0].months", Fields(doc with { Loans = [Loan(421)] }));
        Assert.Contains("vacancyMonths", Fields(doc with { VacancyMonths = 13 }));
        Assert.Contains("price", Fields(doc with { Price = 0m }));
        Assert.Contains("notaryFees", Fields(doc with { NotaryFees = -1m }));
        Assert.Contains("assumptions.savingsClass", Fields(doc with { Assumptions = a with { SavingsClass = "gold" } }));
        var envelope = new ItemId(Guid.NewGuid());
        Assert.Contains("funding[1].item", Fields(doc with { Funding = [new(envelope, 1_000m), new(envelope, 2_000m)] }));
        Assert.Contains("loans[0].deferral.months", Fields(doc with { Loans = [Loan(24, new(DeferralKind.Total, 24))] }));
    }

    [Fact]
    public void A_purchase_in_the_past_is_not_refused() =>
        Assert.Empty(ScenarioRules.Check(Doc() with { PurchaseOn = Today.PlusYears(-1) }, Today));

    [Fact]
    public void The_balance_is_checked_to_within_one_euro()
    {
        var doc = Doc() with
        {
            Price = 300_000m, NotaryFees = 22_000m,
            Loans = [Loan(amount: 300_000m) with { FileFees = 1_000m }],
            Funding = [new(new ItemId(Guid.NewGuid()), 23_000.50m)],
        };
        Assert.Null(ScenarioRules.FundingGap(doc));
        Assert.Equal(3_000m, ScenarioRules.FundingGap(doc with { Funding = [new(doc.Funding[0].Item, 20_000m)] }));
    }

    [Fact]
    public void Defaults_are_the_specs_rates()
    {
        Assert.Equal(0.06m, ScenarioRules.Defaults["equity"]);
        Assert.Equal(0.025m, ScenarioRules.Defaults["money_market"]);
        Assert.Equal(AllocationClasses.All.Order(), ScenarioRules.Defaults.Keys.Order());
    }
}
