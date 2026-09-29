using Nid.Core.Contracts;
using Nid.Core.Domain;
using NodaTime;

namespace Nid.Core.Tests;

public class FactRulesTests
{
    private static readonly ItemId Item = ItemId.New();
    private static readonly LocalDate Day = new(2025, 6, 30);

    private static Money Eur(decimal amount) => Money.Create(amount, Currency.Eur).TryGetValue(out var m) ? m : throw new InvalidOperationException();

    private static NewFlow Flow(decimal amount = 100m, string? category = null, string? externalRef = null, string? note = null)
        => new(Item, Day, FlowKind.Contribution, Eur(amount), category, externalRef, note);

    private static (string Code, string? Field) Failure(UnambitiousFx.Functional.Result result)
    {
        Assert.True(result.TryGetFailure(out var failure), "expected a failure");
        return (failure!.Code, failure.Metadata?.TryGetValue("field", out var field) == true ? field as string : null);
    }

    [Fact]
    public void A_negative_valuation_is_allowed()
        => Assert.True(FactRules.CheckValuation(new NewValuation(Item, Day, Eur(-120.5m), null)).IsSuccess);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_flow_amount_must_be_positive(int amount)
        => Assert.Equal((CoreFactCodes.AmountNotPositive, (string?)null), Failure(FactRules.CheckFlow(Flow(amount))));

    [Fact]
    public void An_amount_beyond_numeric_19_4_is_refused_on_both()
    {
        Assert.Equal((CoreFactCodes.InvalidFact, "amount"), Failure(FactRules.CheckFlow(Flow(1_000_000_000_000_000m))));
        Assert.Equal((CoreFactCodes.InvalidFact, "amount"),
            Failure(FactRules.CheckValuation(new NewValuation(Item, Day, Eur(-1_000_000_000_000_000m), null))));
        Assert.True(FactRules.CheckFlow(Flow(999_999_999_999_999.9999m)).IsSuccess);
    }

    [Theory]
    [InlineData("taxe_fonciere", true)]
    [InlineData("transfer", true)]
    [InlineData("0dividende", true)]
    [InlineData("_lead", false)]
    [InlineData("Majuscule", false)]
    [InlineData("avec-tiret", false)]
    [InlineData("", false)]
    public void A_category_is_a_lowercase_slug(string category, bool valid)
    {
        var result = FactRules.CheckFlow(Flow(category: category));
        if (valid)
            Assert.True(result.IsSuccess);
        else
            Assert.Equal((CoreFactCodes.InvalidFact, "category"), Failure(result));
    }

    [Fact]
    public void A_category_has_at_most_64_characters()
    {
        Assert.True(FactRules.CheckFlow(Flow(category: new string('a', 64))).IsSuccess);
        Assert.Equal((CoreFactCodes.InvalidFact, "category"), Failure(FactRules.CheckFlow(Flow(category: new string('a', 65)))));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("transfer:2025-06-livret-pea", true)]
    [InlineData("ligne\nsuivante", false)]
    public void An_external_reference_has_no_control_character(string externalRef, bool valid)
    {
        var result = FactRules.CheckFlow(Flow(externalRef: externalRef));
        if (valid)
            Assert.True(result.IsSuccess);
        else
            Assert.Equal((CoreFactCodes.InvalidFact, "external_ref"), Failure(result));
    }

    [Fact]
    public void An_external_reference_has_at_most_200_characters()
    {
        Assert.True(FactRules.CheckFlow(Flow(externalRef: new string('x', 200))).IsSuccess);
        Assert.Equal((CoreFactCodes.InvalidFact, "external_ref"), Failure(FactRules.CheckFlow(Flow(externalRef: new string('x', 201)))));
    }

    [Fact]
    public void A_note_has_at_most_2000_characters_on_both()
    {
        Assert.True(FactRules.CheckFlow(Flow(note: new string('n', 2000))).IsSuccess);
        Assert.Equal((CoreFactCodes.InvalidFact, "note"), Failure(FactRules.CheckFlow(Flow(note: new string('n', 2001)))));
        Assert.Equal((CoreFactCodes.InvalidFact, "note"),
            Failure(FactRules.CheckValuation(new NewValuation(Item, Day, Eur(1m), new string('n', 2001)))));
    }

    [Fact]
    public void The_amount_sign_is_checked_before_the_bounds()
        => Assert.Equal((CoreFactCodes.AmountNotPositive, (string?)null), Failure(FactRules.CheckFlow(Flow(0m, category: "BAD"))));
}
