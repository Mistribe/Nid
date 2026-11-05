using Nid.Core.Contracts;
using Nid.Core.Domain.Scenarios;
using NodaTime;

namespace Nid.Core.Tests;

/// <summary>Issue #416: the sale scenario's limits and what the sale leaves the household.</summary>
public class SaleScenarioTests
{
    private static readonly LocalDate Today = new(2026, 10, 22);

    private static SaleScenario Doc() => new(1, new ItemId(Guid.NewGuid()), new(2027, 6, 1), 300_000m, 12_000m, 1_500m, 10);

    private static string[] Fields(SaleScenario doc) => ScenarioRules.Check(doc, Today).Select(e => e.Field).ToArray();

    [Fact]
    public void A_valid_sale_has_no_refusal()
    {
        Assert.Empty(Fields(Doc()));
        Assert.Empty(Fields(Doc() with { SaleOn = Today.PlusYears(-1) })); // taken today, warned about
        Assert.Empty(Fields(Doc() with { SaleOn = Today.PlusYears(10) })); // the horizon's last day
    }

    [Fact]
    public void Each_limit_is_refused_at_its_field()
    {
        Assert.Equal(["price"], Fields(Doc() with { Price = 0m, AgencyFees = 0m }));
        Assert.Equal(["agencyFees"], Fields(Doc() with { AgencyFees = -1m }));
        Assert.Equal(["agencyFees"], Fields(Doc() with { AgencyFees = 300_000m }));
        Assert.Equal(["penalties"], Fields(Doc() with { Penalties = -1m }));
        Assert.Equal(["horizonYears"], Fields(Doc() with { HorizonYears = 0 }));
        Assert.Equal(["horizonYears"], Fields(Doc() with { HorizonYears = 31 }));
        Assert.Equal(["saleOn"], Fields(Doc() with { SaleOn = Today.PlusYears(10).PlusDays(1) }));
    }

    [Fact]
    public void Net_seller_is_the_price_less_fees_tax_loans_and_penalties()
    {
        var outcome = new SaleOutcome(Guid.NewGuid(), 8, 0m, -650m) { Settled = 150_000.004m, LoanPayments = 1_159.92m };

        var result = ScenarioCalculator.Sale(Doc(), 9_876.5m, outcome);

        Assert.Equal(12_000m + 9_876.5m + 1_500m, ScenarioCalculator.SaleCosts(Doc(), 9_876.5m));
        Assert.Equal(300_000m - 12_000m - 9_876.5m - 150_000m - 1_500m, result.NetSeller);
        Assert.Equal((9_876.5m, 150_000m), (result.Tax, result.LoansRepaid));
        Assert.Equal((1_159.92m, -650m, 509.92m), (result.LoanPayments, result.PropertyNet, result.MonthlyChange));
    }

    [Fact]
    public void Net_seller_may_be_negative_when_the_loans_exceed_the_price()
    {
        var outcome = new SaleOutcome(Guid.NewGuid(), 0, 0m, 0m) { Settled = 320_000m };

        Assert.Equal(-33_500m, ScenarioCalculator.Sale(Doc(), 0m, outcome).NetSeller);
    }
}
