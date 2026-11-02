using Nid.Core.Contracts;

namespace Nid.Core.Tests;

/// <summary>3F1 spec §3: what a module's convention says of one flow.</summary>
public class FactConventionTests
{
    private static readonly FactConvention Property = new("realestate", "property", "properties", "realestate.invalid_category", true,
        new Dictionary<FlowKind, IReadOnlyList<string>> { [FlowKind.Expense] = ["works", "other"], [FlowKind.Income] = ["rent"] });

    [Fact]
    public void A_flow_of_an_allowed_kind_and_category_keeps_it()
        => Assert.Null(Property.Refusal(FlowKind.Expense, "works"));

    [Fact]
    public void A_category_of_another_kind_is_refused_at_the_category()
        => Assert.Equal("category", Property.Refusal(FlowKind.Income, "works")?.Member);

    [Fact]
    public void A_kind_the_convention_does_not_list_is_refused_at_the_kind()
        => Assert.Equal("kind", Property.Refusal(FlowKind.Contribution, "works")?.Member);

    [Fact]
    public void A_missing_category_is_refused_only_when_one_is_required()
    {
        Assert.Equal("category", Property.Refusal(FlowKind.Income, null)?.Member);
        Assert.Null((Property with { CategoryRequired = false }).Refusal(FlowKind.Income, null));
    }

    private static readonly FactConvention Envelope = new("investments", "envelope", "envelopes", "investments.invalid_income_category", false,
        new Dictionary<FlowKind, IReadOnlyList<string>> { [FlowKind.Withdrawal] = ["dividend"], [FlowKind.Income] = ["dividend"] }, OpenCategories: true);

    [Fact]
    public void An_open_convention_refuses_a_listed_category_on_a_kind_that_does_not_list_it()
        => Assert.Equal("category", Envelope.Refusal(FlowKind.Contribution, "dividend")?.Member);

    [Fact]
    public void An_open_convention_keeps_the_kind_that_lists_the_category_and_any_other_category_or_kind()
    {
        Assert.Null(Envelope.Refusal(FlowKind.Withdrawal, "dividend"));
        Assert.Null(Envelope.Refusal(FlowKind.Contribution, "import"));
        Assert.Null(Envelope.Refusal(FlowKind.Opening, null));
    }
}
