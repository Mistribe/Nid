using Nid.Banking.Domain;
using NodaTime;

namespace Nid.Banking.Tests;

public class CardSettlementTests
{
    private static Line L(int day, decimal amount) => new(Guid.NewGuid(), Guid.NewGuid(), new LocalDate(2026, 3, day), amount, "EUR", "CB");

    [Fact]
    public void The_sum_of_the_card_lines_settles()
        => Assert.True(CardSettlement.Settles(L(30, -45.50m), [L(3, -20m), L(10, -30.50m), L(12, 5m)], new LocalDate(2026, 3, 1)));

    [Fact]
    public void A_line_on_the_settlement_day_is_outside()
        => Assert.False(CardSettlement.Settles(L(30, -45.50m), [L(3, -20m), L(10, -30.50m), L(30, -9m)], new LocalDate(2026, 3, 1)));

    [Fact]
    public void No_card_line_settles_nothing()
        => Assert.False(CardSettlement.Settles(L(30, 0m), [], new LocalDate(2026, 3, 1)));

    private static readonly Guid Card = Guid.NewGuid(), Alpha = Guid.NewGuid(), Beta = Guid.NewGuid();

    private static Line At(Guid account, int month, int day, decimal amount) => new(Guid.NewGuid(), account, new LocalDate(2026, month, day), amount, "EUR", "x");

    [Fact]
    public void The_only_checking_account_of_the_card_s_connection_is_suggested()
        => Assert.Equal(Alpha, CardParents.Suggest([], [(Alpha, true, []), (Beta, false, [At(Beta, 9, 2, -12m)])]));

    [Fact]
    public void An_immediate_card_s_account_echoes_its_debits()
    {
        IReadOnlyList<Line> card = [At(Card, 9, 2, -12.50m), At(Card, 9, 5, -40m)];

        Assert.Equal(Beta, CardParents.Suggest(card, [(Alpha, true, [At(Alpha, 9, 3, -99m)]), (Beta, true, [At(Beta, 9, 3, -12.50m), At(Beta, 9, 6, -40m)])]));
    }

    [Fact]
    public void A_deferred_card_s_account_pays_its_month()
    {
        IReadOnlyList<Line> card = [At(Card, 9, 2, -12.50m), At(Card, 9, 20, -40m)];

        Assert.Equal(Alpha, CardParents.Suggest(card, [(Alpha, false, [At(Alpha, 10, 1, -52.50m)]), (Beta, false, [At(Beta, 10, 1, -50m)])]));
    }

    [Fact]
    public void A_tie_or_nothing_in_common_suggests_nothing()
    {
        IReadOnlyList<Line> card = [At(Card, 9, 2, -12.50m)];

        Assert.Null(CardParents.Suggest(card, [(Alpha, true, [At(Alpha, 9, 2, -12.50m)]), (Beta, true, [At(Beta, 9, 3, -12.50m)])]));
        Assert.Null(CardParents.Suggest(card, [(Alpha, false, []), (Beta, false, [])]));
    }
}
