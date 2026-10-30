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
}
