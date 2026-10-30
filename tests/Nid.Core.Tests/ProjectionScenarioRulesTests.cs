using Nid.Core.Contracts;
using Nid.Core.Domain.Projection;
using NodaTime;

namespace Nid.Core.Tests;

public class ProjectionScenarioRulesTests
{
    private static readonly PersonId Alice = PersonId.New();
    private static readonly ItemId Home = ItemId.New();
    private static readonly ItemId Loan = ItemId.New();
    private static readonly EventDate Jan2030 = new(new LocalDate(2030, 1, 1), null, null);

    private static ProjectionEvent Blank(string kind) => new(
        Guid.NewGuid(), kind, Jan2030, null, null, null, null, null, null, null, null, null, null, null, null);

    private static ProjectionEvent Income() => Blank(ProjectionEventKinds.IncomeChange) with { MonthlyAmount = 500m };
    private static ProjectionEvent Savings() => Blank(ProjectionEventKinds.SavingsChange) with { MonthlyAmount = -100m };

    private static ProjectionEvent Repayment() => Blank(ProjectionEventKinds.EarlyRepayment) with
    {
        Amount = 10_000m, Split = ProjectionSplits.HighestRate, Effect = ProjectionEffects.ReduceTerm, Penalty = 0m,
    };

    private static ProjectionEvent Sale(ItemId? property = null) => Blank(ProjectionEventKinds.PropertySale) with
    {
        Property = property ?? Home, Costs = 0m,
    };

    private static ProjectionEvent Purchase() => Blank(ProjectionEventKinds.PropertyPurchase) with { Scenario = ItemId.New() };

    private static ProjectionScenario Doc(params ProjectionEvent[] events) => new(
        1, new ScenarioHorizon(20, null), new ProjectionOverrides(null, null, null, null, null), events);

    private static string Only(ProjectionScenario doc) => Assert.Single(ProjectionScenarioRules.Check(doc)).Field;

    [Fact]
    public void Valid_document_has_no_refusal()
        => Assert.Empty(ProjectionScenarioRules.Check(Doc(Income(), Savings(), Repayment(), Sale(), Purchase())));

    [Fact]
    public void Thirty_one_events_are_refused()
        => Assert.Equal("events", Only(Doc(Enumerable.Range(0, 31).Select(_ => Income()).ToArray())));

    [Fact]
    public void Repayment_without_amount_is_refused()
        => Assert.Equal("events[0].amount", Only(Doc(Repayment() with { Amount = null })));

    [Fact]
    public void Chosen_split_needs_a_loan()
        => Assert.Equal("events[0].loan", Only(Doc(Repayment() with { Split = ProjectionSplits.Chosen })));

    [Fact]
    public void Loan_without_chosen_split_is_refused()
        => Assert.Equal("events[0].loan", Only(Doc(Repayment() with { Loan = Loan })));

    [Fact]
    public void Two_sales_of_one_property_are_refused()
        => Assert.Equal("events[1].property", Only(Doc(Sale(), Sale())));

    [Fact]
    public void Age_17_is_refused()
        => Assert.Equal("events[0].start.age", Only(Doc(Income() with { Start = new(null, Alice, 17) })));

    [Fact]
    public void Until_before_start_is_refused()
        => Assert.Equal("events[0].until", Only(Doc(Income() with { Until = new(new LocalDate(2029, 1, 1), null, null) })));

    [Fact]
    public void Field_of_another_kind_is_refused()
        => Assert.Equal("events[0].price", Only(Doc(Income() with { Price = 1m })));

    [Fact]
    public void Horizon_needs_years_or_a_person()
    {
        Assert.Equal("horizon", Only(Doc() with { Horizon = new(null, null) }));
        Assert.Equal("horizon", Only(Doc() with { Horizon = new(10, Alice) }));
        Assert.Equal("horizon", Only(Doc() with { Horizon = new(41, null) }));
    }

    [Fact]
    public void At_age_resolves_to_the_birthday_month_end()
    {
        PersonInfo? Born(PersonId id) => new(id, PersonKind.Adult, "A", null, 0, BirthDate: new LocalDate(1990, 5, 10));

        Assert.Equal(new LocalDate(2054, 5, 31), ProjectionScenarioRules.Resolve(new(null, Alice, 64), Born));
        Assert.Equal(new LocalDate(2030, 1, 1), ProjectionScenarioRules.Resolve(Jan2030, _ => null));
    }

    [Fact]
    public void At_age_without_birth_date_is_null()
    {
        Assert.Null(ProjectionScenarioRules.Resolve(new(null, Alice, 64), id => new(id, PersonKind.Adult, "A", null, 0)));
        Assert.Null(ProjectionScenarioRules.Resolve(new(null, Alice, 64), _ => null));
    }
}
