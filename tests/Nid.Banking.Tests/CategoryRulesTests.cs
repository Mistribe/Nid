using Nid.Banking.Contracts;
using Nid.Banking.Domain;
using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Banking.Tests;

public class CategoryRulesTests
{
    private static readonly Guid Acc = Guid.NewGuid();
    private static readonly Instant T0 = Instant.FromUtc(2026, 1, 1, 0, 0);

    private static CategoryRule Rule(string pattern = "CARREFOUR", Guid? account = null, RuleDirection? direction = null,
        decimal? min = null, decimal? max = null, Instant? at = null, Guid? id = null)
        => new(id ?? Guid.NewGuid(), pattern, account, direction, min, max, Guid.NewGuid(), PersonId.New(), at ?? T0, null);

    [Fact]
    public void Normalizes_the_pattern()
    {
        Assert.True(CategoryRules.NormalizePattern("  Carrefour  City ").TryGetValue(out var p));
        Assert.Equal("CARREFOUR CITY", p);
    }

    [Theory]
    [InlineData("cb")]
    [InlineData("")]
    [InlineData("12")]
    [InlineData("-- 12")]
    public void Refuses_a_short_pattern(string raw)
    {
        var result = CategoryRules.NormalizePattern(raw);
        Assert.True(result.TryGetFailure(out var failure));
        Assert.Equal(BankingCodes.RulePattern, failure.Code);
    }

    [Theory]
    [InlineData("NETFLIX 123 PARIS", "NETFLIX")]
    [InlineData("EDF CLIENTS PARTICULIE ECHEANCE ELECTRICITE OCTOBRE", "EDF CLIENTS")]
    [InlineData("CARREFOUR CITY", "CARREFOUR CITY")]
    [InlineData("AMAZON EU SARL", "AMAZON EU")]
    [InlineData("SOCIETE EXEMPLE SAS", "SOCIETE EXEMPLE")]
    [InlineData("NETFLIX", "NETFLIX")]
    [InlineData("CARTE 12/03/26 SHOP ONLINE CB*0000", "SHOP ONLINE")]
    [InlineData("CARTE 12/03 SHOP", "SHOP")]
    [InlineData("ASSOCIATION SPORTIVE DES AMIS DU QUARTIER NORD POUR LA PRATIQUE DU VELO", "ASSOCIATION SPORTIVE")]
    [InlineData("123456 EDF", null)]
    [InlineData("A 12 CARREFOUR", null)]
    [InlineData("", null)]
    public void Suggests_the_label_s_leading_words(string label, string? expected)
        => Assert.Equal(expected, CategoryRules.Suggest(label));

    [Theory]
    [InlineData("CARREFOUR CITY")]
    [InlineData("NETFLIX 123 PARIS")]
    [InlineData("SNCF INTERNET 12/10")]
    [InlineData("CB CB SHOP")]
    [InlineData("CARTE 12/03/26 SHOP ONLINE CB*0000")]
    [InlineData("AMAZON PAYMENTS EUROPE S.C.A. 4521")]
    [InlineData("PRLV SEPA Électricité de France 0042")]
    [InlineData("ASSOCIATION SPORTIVE DES AMIS DU QUARTIER NORD POUR LA PRATIQUE DU VELO")]
    [InlineData("PRLV SEPA EDF CLIENTS PARTICULIE Échéance électricité octobre")]
    public void A_suggestion_matches_the_line_it_comes_from(string raw)
    {
        var label = TransactionLabel.Normalize(raw);
        var suggested = CategoryRules.Suggest(label);
        Assert.NotNull(suggested);
        Assert.Contains(suggested, label, StringComparison.Ordinal);
        Assert.True(suggested.Length <= CategoryRules.MaxPattern);
    }

    [Fact]
    public void Next_month_s_line_gets_the_same_suggestion()
        => Assert.Equal(
            CategoryRules.Suggest(TransactionLabel.Normalize("PRLV SEPA EDF CLIENTS PARTICULIE Échéance électricité octobre")),
            CategoryRules.Suggest(TransactionLabel.Normalize("PRLV SEPA EDF CLIENTS PARTICULIE Échéance électricité novembre")));

    [Fact]
    public void Keeps_a_two_letter_pattern()
    {
        Assert.True(CategoryRules.NormalizePattern("bp").TryGetValue(out var p));
        Assert.Equal("BP", p);
    }

    [Fact]
    public void Refuses_a_long_pattern()
        => Assert.True(CategoryRules.NormalizePattern(new string('a', 61)).IsFailure);

    [Fact]
    public void Matches_the_label_or_the_counterparty()
    {
        Assert.True(CategoryRules.Matches(Rule(), Acc, -5m, "CARREFOUR CITY 12", null));
        Assert.True(CategoryRules.Matches(Rule(), Acc, -5m, "PAIEMENT", "CARREFOUR"));
        Assert.False(CategoryRules.Matches(Rule(), Acc, -5m, "PAIEMENT", "AUCHAN"));
        Assert.False(CategoryRules.Matches(Rule(), Acc, -5m, "PAIEMENT", null));
    }

    [Fact]
    public void Filters_by_account_and_direction()
    {
        Assert.False(CategoryRules.Matches(Rule(account: Guid.NewGuid()), Acc, -5m, "CARREFOUR", null));
        Assert.True(CategoryRules.Matches(Rule(account: Acc), Acc, -5m, "CARREFOUR", null));
        Assert.False(CategoryRules.Matches(Rule(direction: RuleDirection.Debit), Acc, 5m, "CARREFOUR", null));
        Assert.True(CategoryRules.Matches(Rule(direction: RuleDirection.Debit), Acc, -5m, "CARREFOUR", null));
        Assert.False(CategoryRules.Matches(Rule(direction: RuleDirection.Credit), Acc, -5m, "CARREFOUR", null));
        Assert.True(CategoryRules.Matches(Rule(direction: RuleDirection.Credit), Acc, 5m, "CARREFOUR", null));
    }

    [Theory]
    [InlineData(-10, true)]
    [InlineData(-20, true)]
    [InlineData(15, true)]
    [InlineData(-9.99, false)]
    [InlineData(-20.01, false)]
    public void Bounds_are_inclusive_on_the_absolute_amount(double amount, bool expected)
        => Assert.Equal(expected, CategoryRules.Matches(Rule(min: 10m, max: 20m), Acc, (decimal)amount, "CARREFOUR", null));

    [Fact]
    public void Pick_prefers_the_account_over_a_longer_newer_rule()
    {
        var restricted = Rule("CARREF", account: Acc);
        var other = Rule("CARREFOUR", at: T0.Plus(NodaTime.Duration.FromDays(1)), min: 1m);
        Assert.Same(restricted, CategoryRules.Pick([other, restricted], Acc, -5m, "CARREFOUR", null));
    }

    [Fact]
    public void Pick_prefers_bounds_then_a_longer_pattern_then_the_newest_then_the_smallest_id()
    {
        var bounded = Rule("CARRE", min: 1m);
        var longer = Rule("CARREFOUR");
        Assert.Same(bounded, CategoryRules.Pick([longer, bounded], Acc, -5m, "CARREFOUR", null));
        Assert.Same(longer, CategoryRules.Pick([Rule("CARRE"), longer], Acc, -5m, "CARREFOUR", null));

        var older = Rule();
        var newer = Rule(at: T0.Plus(NodaTime.Duration.FromDays(1)));
        Assert.Same(newer, CategoryRules.Pick([older, newer], Acc, -5m, "CARREFOUR", null));

        var low = Rule(id: new Guid(1, 0, 0, new byte[8]));
        var high = Rule(id: new Guid(2, 0, 0, new byte[8]));
        Assert.Same(low, CategoryRules.Pick([high, low], Acc, -5m, "CARREFOUR", null));
        Assert.Null(CategoryRules.Pick([], Acc, -5m, "CARREFOUR", null));
    }
}
