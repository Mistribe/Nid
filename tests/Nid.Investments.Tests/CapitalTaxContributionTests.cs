using Nid.Core.Contracts;
using Nid.Tax.Contracts;
using Nid.Investments.Application;
using Nid.Investments.Contracts;
using NodaTime;

namespace Nid.Investments.Tests;

public class CapitalTaxContributionTests
{
    private static LocalDate D(int y, int m, int d) => new(y, m, d);

    [Fact]
    public void The_tax_line_of_a_bank_fed_livret_is_its_interest()
    {
        var livret = new EnvelopeFacts("Livret A", EnvelopeKind.Savings)
            .Flow(D(2024, 12, 31), FlowKind.Opening, 1_000m).Value(D(2024, 12, 31), 1_000m, FactSource.BankApi)
            .Flow(D(2025, 6, 15), FlowKind.Contribution, 100m, reference: "bank:11111111-1111-1111-1111-111111111111")
            .Flow(D(2025, 12, 31), FlowKind.Income, 12.5m, FlowCategories.Interest, "bank:22222222-2222-2222-2222-222222222222:interest")
            .Value(D(2025, 12, 31), 1_112.5m, FactSource.BankApi).Build();

        var line = CapitalTaxContribution.SavingsLine(livret, D(2025, 1, 1), D(2025, 12, 31));

        Assert.Equal((TaxLineCodes.SavingsInterest, 12.5m), (line.Code, line.Amount));
    }

    [Fact]
    public void A_bank_value_of_a_prior_year_alone_leaves_a_hand_typed_year_counted()
    {
        var livret = new EnvelopeFacts("Livret A", EnvelopeKind.Savings)
            .Flow(D(2024, 12, 31), FlowKind.Opening, 1_000m).Value(D(2024, 12, 31), 1_000m, FactSource.BankApi)
            .Value(D(2025, 12, 31), 1_012.5m).Build();

        var line = CapitalTaxContribution.SavingsLine(livret, D(2025, 1, 1), D(2025, 12, 31));

        Assert.Equal((TaxLineCodes.SavingsInterest, 12.5m), (line.Code, line.Amount));
    }

    [Fact]
    public void A_bank_deposit_alone_leaves_the_interest_unknown_until_the_opening_is_typed()
    {
        // Linked in January with 10 000 on it: counted, the whole balance would read as the year's interest.
        EnvelopePerformance.Facts Livret(bool opening)
        {
            var facts = new EnvelopeFacts("Livret A", EnvelopeKind.Savings).Value(D(2026, 1, 31), 10_000m, FactSource.BankApi)
                .Flow(D(2026, 2, 10), FlowKind.Contribution, 100m, reference: "bank:33333333-3333-3333-3333-333333333333").Value(D(2026, 12, 31), 10_150m, FactSource.BankApi);
            return (opening ? facts.Flow(D(2026, 1, 31), FlowKind.Opening, 10_000m) : facts).Build();
        }

        Assert.Equal(TaxLineCodes.SavingsInterestUnknown, CapitalTaxContribution.SavingsLine(Livret(opening: false), D(2026, 1, 1), D(2026, 12, 31)).Code);
        var line = CapitalTaxContribution.SavingsLine(Livret(opening: true), D(2026, 1, 1), D(2026, 12, 31));
        Assert.Equal((TaxLineCodes.SavingsInterest, 50m), (line.Code, line.Amount));
    }

    [Fact]
    public void The_tax_line_of_a_bank_fed_livret_without_bank_flows_stays_unknown()
    {
        var livret = new EnvelopeFacts("Livret A", EnvelopeKind.Savings)
            .Flow(D(2024, 12, 31), FlowKind.Opening, 1_000m).Value(D(2024, 12, 31), 1_000m)
            .Value(D(2025, 12, 31), 1_012.5m, FactSource.BankApi).Build();

        Assert.Equal(TaxLineCodes.SavingsInterestUnknown, CapitalTaxContribution.SavingsLine(livret, D(2025, 1, 1), D(2025, 12, 31)).Code);
    }
}
