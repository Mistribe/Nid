using Nid.Core.Contracts;
using Nid.Investments.Contracts;
using Nid.Investments.Domain;
using NodaTime;

namespace Nid.Investments.Tests;

/// <summary>Issue #180: an SCPI held directly, its value at a date and when its shares start earning.</summary>
public class ScpiMathTests
{
    [Theory]
    [InlineData("2026-01-15", 3, "2026-05-01")]
    [InlineData("2026-01-01", 0, "2026-02-01")]
    [InlineData("2026-11-20", 3, "2027-03-01")]
    [InlineData("2026-12-31", 12, "2028-01-01")]
    public void Shares_start_earning_on_the_first_day_of_the_month_after_the_delay(string subscribed, int delay, string expected)
        => Assert.Equal(LocalDate.FromDateTime(DateTime.Parse(expected)), ScpiMath.EntitledFrom(LocalDate.FromDateTime(DateTime.Parse(subscribed)), delay));

    [Fact]
    public void A_holding_shows_its_next_date_to_come_else_its_last_one()
    {
        LocalDate[] purchases = [new(2026, 1, 15), new(2026, 9, 10)];

        Assert.Equal(new LocalDate(2027, 1, 1), ScpiMath.EntitledFrom(purchases, 3, today: new LocalDate(2026, 11, 21)));
        Assert.Equal(new LocalDate(2027, 1, 1), ScpiMath.EntitledFrom(purchases, 3, today: new LocalDate(2027, 2, 1)));
        Assert.Equal(new LocalDate(2026, 5, 1), ScpiMath.EntitledFrom(purchases, 3, today: new LocalDate(2026, 3, 1)));
        Assert.Null(ScpiMath.EntitledFrom([], 3, today: new LocalDate(2026, 11, 21)));
    }

    [Fact]
    public void An_scpi_is_worth_its_shares_at_the_withdrawal_price_of_its_latest_bulletin()
    {
        var scpi = Guid.NewGuid();
        var envelope = ItemId.New();
        HoldingSnapshot[] bulletins =
        [
            new(Guid.NewGuid(), envelope, new LocalDate(2026, 3, 31), null, true, [new SnapshotLine(scpi, 10m, 200.00m, 2_000.00m, null)]),
            new(Guid.NewGuid(), envelope, new LocalDate(2026, 6, 30), null, true, [new SnapshotLine(scpi, 12m, 195.50m, 2_346.00m, null)]),
        ];

        Assert.Equal((new LocalDate(2026, 3, 31), 2_000.00m), HoldingMath.ValueAt(scpi, bulletins, new LocalDate(2026, 5, 15)));
        Assert.Equal((new LocalDate(2026, 6, 30), 2_346.00m), HoldingMath.ValueAt(scpi, bulletins, new LocalDate(2026, 6, 30)));
        Assert.Null(HoldingMath.ValueAt(scpi, bulletins, new LocalDate(2026, 1, 1)));
        Assert.Empty(HoldingMath.Warnings(bulletins[1], 2_346.00m));
    }
}
