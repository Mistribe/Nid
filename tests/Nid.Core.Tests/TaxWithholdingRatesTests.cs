using Nid.Core.Application.Tax;
using Nid.Core.Contracts;
using Nid.Core.Domain.Tax;

namespace Nid.Core.Tests;

/// <summary>Income #408: each declarant's rates go to the person they were computed for.</summary>
public class TaxWithholdingRatesTests
{
    [Fact]
    public void Rates_follow_each_declarant_by_person_not_by_position()
    {
        PersonInfo Adult(string name) => new(PersonId.New(), PersonKind.Adult, name, null, 0);
        var (anne, bob) = (Adult("Anne"), Adult("Bob"));
        // Bob declares first and earns alone; the household lists Anne first.
        var result = TaxCalculator.Compute(new TaxInputs(2025,
            [new("Bob", 0m, EmploymentStatus.NonExecutive, 60_000m, 0m, Person: bob.Id.Value),
                new("Anne", 0m, EmploymentStatus.NonExecutive, 0m, 0m, Person: anne.Id.Value)],
            0, 0, 0m, 0m, 0m, null, 0m, 0m, 0m, 0m, 0m, 0m, 0m));

        var rates = TaxWithholdingRates.Of(result, 14m, [anne, bob]);

        Assert.Equal((bob.Id, "Bob"), (Assert.Single(rates).Person, rates[0].Name));
    }

    [Fact]
    public void A_declarant_without_a_person_is_left_out()
    {
        var result = TaxCalculator.Compute(new TaxInputs(2025, [new("", 0m, EmploymentStatus.NonExecutive, 60_000m, 0m)],
            0, 0, 0m, 0m, 0m, null, 0m, 0m, 0m, 0m, 0m, 0m, 0m));

        Assert.Empty(TaxWithholdingRates.Of(result, 14m, []));
    }
}
