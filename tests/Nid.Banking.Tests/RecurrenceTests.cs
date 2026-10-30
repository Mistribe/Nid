using Nid.Banking.Domain;
using NodaTime;

namespace Nid.Banking.Tests;

public class RecurrenceTests
{
    private static readonly Guid Account = Guid.NewGuid();
    private static readonly LocalDate Today = new(2026, 3, 10);

    private static Line L(LocalDate on, decimal amount, string stem = "NETFLIX") => new(Guid.NewGuid(), Account, on, -amount, "EUR", stem);

    private static Line[] Monthly(params decimal[] amounts)
        => [L(new(2026, 1, 5), amounts[0]), L(new(2026, 2, 4), amounts[1]), L(new(2026, 3, 6), amounts[2])];

    [Fact]
    public void Three_monthly_debits_make_a_group()
    {
        var group = Assert.Single(Recurrence.Find(Monthly(15m, 15m, 15.90m), Today));
        Assert.Equal((Account, "NETFLIX", Cadence.Monthly), (group.Account, group.Stem, group.Cadence));
        Assert.Equal(3, group.Lines.Count);
    }

    [Fact]
    public void Two_monthly_debits_are_not_enough()
        => Assert.Empty(Recurrence.Find(Monthly(15m, 15m, 15m)[1..], Today));

    [Fact]
    public void An_amount_off_by_more_than_ten_percent_breaks_the_group()
        => Assert.Empty(Recurrence.Find(Monthly(15m, 15m, 17m), Today));

    [Fact]
    public void Three_quarterly_debits_make_a_group()
    {
        Line[] lines = [L(new(2025, 6, 1), 30m), L(new(2025, 9, 1), 30m), L(new(2025, 12, 1), 30m)];
        Assert.Equal(Cadence.Quarterly, Assert.Single(Recurrence.Find(lines, new LocalDate(2026, 1, 10))).Cadence);
    }

    [Fact]
    public void Two_yearly_debits_make_a_group()
    {
        Line[] lines = [L(new(2025, 11, 2), 99m), L(new(2026, 11, 1), 99m)];
        Assert.Equal(Cadence.Yearly, Assert.Single(Recurrence.Find(lines, new LocalDate(2026, 11, 5))).Cadence);
    }

    [Fact]
    public void A_stopped_group_is_not_proposed()
        => Assert.Empty(Recurrence.Find(Monthly(15m, 15m, 15m), new LocalDate(2026, 4, 25)));

    [Fact]
    public void An_irregular_group_is_not_proposed()
        => Assert.Empty(Recurrence.Find([L(new(2026, 1, 5), 15m), L(new(2026, 1, 20), 15m), L(new(2026, 3, 6), 15m)], Today));

    [Fact]
    public void Empty_stems_never_group()
        => Assert.Empty(Recurrence.Find(Monthly(15m, 15m, 15m).Select(l => l with { Stem = "" }).ToArray(), Today));

    [Fact]
    public void Lines_older_than_thirteen_months_are_ignored()
        => Assert.Empty(Recurrence.Find([L(new(2024, 12, 1), 15m), L(new(2025, 1, 1), 15m), L(new(2025, 2, 1), 15m)], new LocalDate(2026, 3, 10)));
}
