using Nid.Core.Contracts;
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
    public void The_tax_line_of_a_bank_fed_livret_without_bank_flows_stays_unknown()
    {
        var livret = new EnvelopeFacts("Livret A", EnvelopeKind.Savings)
            .Flow(D(2024, 12, 31), FlowKind.Opening, 1_000m).Value(D(2024, 12, 31), 1_000m)
            .Value(D(2025, 12, 31), 1_012.5m, FactSource.BankApi).Build();

        Assert.Equal(TaxLineCodes.SavingsInterestUnknown, CapitalTaxContribution.SavingsLine(livret, D(2025, 1, 1), D(2025, 12, 31)).Code);
    }
}
