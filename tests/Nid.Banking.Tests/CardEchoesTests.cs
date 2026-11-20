using Nid.Banking.Domain;
using NodaTime;

namespace Nid.Banking.Tests;

/// <summary>#416: an immediate card's line and its echo on the checking account.</summary>
public class CardEchoesTests
{
    private static readonly Guid Courant = Guid.NewGuid(), Carte = Guid.NewGuid();

    private static Line L(Guid account, int day, decimal amount, string currency = "EUR")
        => new(Guid.NewGuid(), account, new LocalDate(2026, 3, day), amount, currency, "CB");

    [Fact]
    public void The_same_debit_within_three_days_is_an_echo()
    {
        var (echo, card) = (L(Courant, 14, -42m), L(Carte, 12, -42m));

        Assert.Equal([(echo.Id, card.Id)], CardEchoes.Pair([echo, L(Courant, 20, -42m), L(Courant, 13, -41m)], [card]));
    }

    [Fact]
    public void A_debit_too_far_another_currency_or_a_credit_is_none()
        => Assert.Empty(CardEchoes.Pair([L(Courant, 16, -42m), L(Courant, 12, -42m, "USD"), L(Courant, 12, 42m)], [L(Carte, 12, -42m)]));

    [Fact]
    public void Each_line_pairs_once_nearest_first()
    {
        var (first, second) = (L(Carte, 10, -42m), L(Carte, 12, -42m));
        var (echoFirst, echoSecond) = (L(Courant, 10, -42m), L(Courant, 13, -42m));

        Assert.Equal([(echoFirst.Id, first.Id), (echoSecond.Id, second.Id)],
            CardEchoes.Pair([echoFirst, echoSecond], [first, second]).OrderBy(p => p.Echo == echoSecond.Id).ToList());
    }

    [Fact]
    public void Two_identical_purchases_the_same_day_are_a_tie()
        => Assert.Empty(CardEchoes.Pair([L(Courant, 12, -42m), L(Courant, 12, -42m)], [L(Carte, 12, -42m), L(Carte, 12, -42m)]));

    [Fact]
    public void A_tied_line_does_not_fall_back_on_a_farther_one()
        => Assert.Empty(CardEchoes.Pair([L(Courant, 12, -42m)], [L(Carte, 11, -42m), L(Carte, 13, -42m)]));
}
