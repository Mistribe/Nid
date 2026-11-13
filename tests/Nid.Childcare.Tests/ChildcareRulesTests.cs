using Nid.Childcare.Domain;
using NodaTime;

namespace Nid.Childcare.Tests;

public class ChildcareRulesTests
{
    [Fact]
    public void First_half_of_2026()
    {
        var rules = ChildcareRules.For(new LocalDate(2026, 5, 31));
        Assert.Equal(4.25m, rules.MinimumGuaranteed);
        Assert.Equal(3.83m, rules.Upkeep9hMinimum);
    }

    [Fact]
    public void From_June_2026()
    {
        Assert.Equal(4.35m, ChildcareRules.For(new LocalDate(2026, 6, 1)).MinimumGuaranteed);
        Assert.Equal(4.35m, ChildcareRules.For(new LocalDate(2027, 3, 1)).MinimumGuaranteed);
    }

    [Fact]
    public void Before_the_first_period_throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => ChildcareRules.For(new LocalDate(2024, 10, 31)));
}
