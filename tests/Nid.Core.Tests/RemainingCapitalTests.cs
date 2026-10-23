using Nid.Core.Domain.Tax;
using NodaTime;

namespace Nid.Core.Tests;

/// <summary>Sale tax spec §4.4, D2, D8b, D16: the premiums still in a contract after its partial withdrawals.</summary>
public class RemainingCapitalTests
{
    private static readonly LocalDate Until = new(2026, 10, 23);

    private static CapitalFlow Pay(int y, int m, int d, decimal amount) => new(new LocalDate(y, m, d), CapitalFlowKind.Payment, amount);
    private static CapitalFlow Take(int y, int m, int d, decimal amount) => new(new LocalDate(y, m, d), CapitalFlowKind.Withdrawal, amount);
    private static ValuePoint Value(int y, int m, int d, decimal value) => new(new LocalDate(y, m, d), value);

    private static RemainingCapitalResult Replay(CapitalFlow[] flows, ValuePoint[] values, decimal before2017AtStart = 0m, LocalDate? until = null) =>
        RemainingCapital.Replay(before2017AtStart, flows, values, until ?? Until);

    [Fact]
    public void Payments_only()
    {
        var r = Replay([Pay(2018, 1, 1, 10_000m), Pay(2019, 1, 1, 5_000m)], []);

        Assert.Equal((0m, 15_000m), (r.Before2017, r.After2017));
        Assert.False(r.Approximate);
        Assert.Empty(r.TransferredOut);
    }

    [Fact]
    public void Payments_are_split_at_27_September_2017()
    {
        // D8b: 4 000 of the opening amount are pre-2017 premiums, the other 6 000 post-2017 whatever its date.
        var r = Replay([Pay(2016, 1, 15, 10_000m), Pay(2017, 9, 26, 1_000m), Pay(2017, 9, 27, 2_000m)], [], before2017AtStart: 4_000m);

        Assert.Equal((5_000m, 8_000m), (r.Before2017, r.After2017));
    }

    [Fact]
    public void The_pre_2017_part_of_the_opening_amount_is_capped_at_that_amount()
    {
        var r = Replay([Pay(2016, 1, 15, 10_000m), Pay(2018, 1, 1, 3_000m)], [], before2017AtStart: 15_000m);

        Assert.Equal((10_000m, 3_000m), (r.Before2017, r.After2017));
    }

    [Fact]
    public void A_value_includes_the_flows_of_its_own_date()
    {
        // The first value, 100 000 on 01/01, already holds the opening: V = 100 000, the withdrawal is all capital.
        var r = Replay([Pay(2018, 1, 1, 100_000m), Take(2018, 2, 1, 10_000m)], [Value(2018, 1, 1, 100_000m)]);

        Assert.Equal(90_000m, r.After2017);
        Assert.False(r.Approximate);
    }

    [Fact]
    public void A_second_withdrawal_on_the_value_date_sees_the_value_less_the_first()
    {
        // The 90 000 value is after both: 1st V = 90 000 + 25 000 + 10 000 = 125 000, 25 000 x 100 000 / 125 000 = 20 000 -> 80 000;
        // 2nd V = 90 000 + 10 000 = 100 000, 10 000 x 80 000 / 100 000 = 8 000 -> 72 000
        var r = Replay(
            [Pay(2018, 1, 1, 100_000m), Take(2020, 7, 1, 25_000m), Take(2020, 7, 1, 10_000m)], [Value(2020, 7, 1, 90_000m)]);

        Assert.Equal(72_000m, r.After2017);
    }

    [Fact]
    public void One_withdrawal_with_a_value_the_day_before()
    {
        // 25 000 x 100 000 / 125 000 = 20 000 of capital
        var r = Replay([Pay(2018, 1, 1, 100_000m), Take(2020, 7, 1, 25_000m)], [Value(2020, 6, 30, 125_000m)]);

        Assert.Equal((0m, 80_000m), (r.Before2017, r.After2017));
        Assert.False(r.Approximate);
    }

    [Fact]
    public void A_value_on_the_day_of_the_withdrawal_is_the_end_of_that_day()
    {
        // The 100 000 value already holds the withdrawal: V = 100 000 + 25 000 = 125 000, 25 000 x 100 000 / 125 000 = 20 000 -> 80 000
        var r = Replay([Pay(2018, 1, 1, 100_000m), Take(2020, 7, 1, 25_000m)], [Value(2020, 7, 1, 100_000m)]);

        Assert.Equal(80_000m, r.After2017);
        Assert.False(r.Approximate);
    }

    [Fact]
    public void A_value_left_after_a_large_withdrawal_that_day_keeps_part_of_the_capital()
    {
        // V = 25 000 + 100 000 = 125 000, 100 000 x 100 000 / 125 000 = 80 000 -> 20 000
        var r = Replay([Pay(2018, 1, 1, 100_000m), Take(2020, 7, 1, 100_000m)], [Value(2020, 7, 1, 25_000m)]);

        Assert.Equal(20_000m, r.After2017);
        Assert.False(r.Approximate);
    }

    [Fact]
    public void Same_day_payments_come_before_withdrawals()
    {
        // Payment first: capital 120 000, V = 120 000 + 20 000 = 140 000, 28 000 x 120 000 / 140 000 = 24 000 -> 96 000
        // (withdrawal first would give 100 000 - 23 333.33 + 20 000 = 96 666.67).
        var r = Replay(
            [Pay(2018, 1, 1, 100_000m), Take(2020, 7, 1, 28_000m), Pay(2020, 7, 1, 20_000m)], [Value(2020, 6, 30, 120_000m)]);

        Assert.Equal(96_000m, r.After2017);
    }

    // Worked example: a contract opened 2016-05-01 with 60 000, 40 000 of them pre-2017 premiums (D8b).
    //   2016-05-01 opening 60 000      -> before 40 000, after 20 000
    //   2018-03-01 payment 20 000      -> after 40 000; capital 80 000
    //   2019-12-31 value 96 000
    //   2020-02-01 payment 4 000       -> after 44 000; capital 84 000
    //   2020-03-01 withdrawal 10 000   V = 96 000 + 4 000 = 100 000 (value 61 days old), share 10 000 / 100 000 = 10 %
    //                                  -> before 40 000 - 4 000 = 36 000, after 44 000 - 4 400 = 39 600; capital 75 600
    //   2021-06-30 value 90 000
    //   2021-07-15 withdrawal 18 000   V = 90 000 (15 days old), share 18 000 / 90 000 = 20 %
    //                                  -> before 36 000 - 7 200 = 28 800, after 39 600 - 7 920 = 31 680; capital 60 480
    [Fact]
    public void Two_withdrawals()
    {
        var r = Replay(
            [Pay(2016, 5, 1, 60_000m), Pay(2018, 3, 1, 20_000m), Pay(2020, 2, 1, 4_000m), Take(2020, 3, 1, 10_000m), Take(2021, 7, 15, 18_000m)],
            [Value(2019, 12, 31, 96_000m), Value(2021, 6, 30, 90_000m)],
            before2017AtStart: 40_000m);

        Assert.Equal((28_800m, 31_680m), (r.Before2017, r.After2017));
        Assert.False(r.Approximate);
    }

    [Fact]
    public void A_withdrawal_without_a_recent_value_is_approximate()
    {
        // 1st: V = 125 000 (15 days old), 25 000 x 100 000 / 125 000 = 20 000 -> 80 000
        // 2nd: the latest value is 153 days old, V = 125 000 - 25 000 = 100 000, 10 000 x 80 000 / 100 000 = 8 000 -> 72 000
        var r = Replay(
            [Pay(2018, 1, 1, 100_000m), Take(2020, 1, 15, 25_000m), Take(2020, 6, 1, 10_000m)], [Value(2019, 12, 31, 125_000m)]);

        Assert.Equal(72_000m, r.After2017);
        Assert.True(r.Approximate);
    }

    [Theory]
    [InlineData(3, 31, false)] // 90 days after 2020-01-01
    [InlineData(4, 1, true)]
    public void A_value_more_than_90_days_old_is_approximate(int month, int day, bool approximate)
    {
        var r = Replay([Pay(2018, 1, 1, 100_000m), Take(2020, month, day, 25_000m)], [Value(2020, 1, 1, 125_000m)]);

        Assert.Equal(80_000m, r.After2017);
        Assert.Equal(approximate, r.Approximate);
    }

    [Fact]
    public void Without_any_value_a_withdrawal_is_all_capital_and_approximate()
    {
        var some = Replay([Pay(2018, 1, 1, 100_000m), Take(2020, 7, 1, 30_000m)], []);
        var all = Replay([Pay(2018, 1, 1, 100_000m), Take(2020, 7, 1, 150_000m)], [Value(2020, 8, 1, 200_000m)]);

        Assert.Equal(70_000m, some.After2017);
        Assert.True(some.Approximate);
        Assert.Equal((0m, 0m), (all.Before2017, all.After2017));
        Assert.True(all.Approximate);
    }

    [Fact]
    public void A_withdrawal_larger_than_the_value_removes_all_the_capital()
    {
        var r = Replay([Pay(2016, 1, 1, 100_000m), Take(2020, 7, 1, 60_000m)], [Value(2020, 6, 30, 50_000m)], before2017AtStart: 30_000m);

        Assert.Equal((0m, 0m), (r.Before2017, r.After2017));
        Assert.False(r.Approximate);
    }

    [Fact]
    public void A_value_of_zero_or_less_counts_as_no_value()
    {
        var r = Replay([Pay(2018, 1, 1, 100_000m), Take(2020, 7, 1, 30_000m)], [Value(2020, 6, 30, 0m)]);

        Assert.Equal(70_000m, r.After2017);
        Assert.True(r.Approximate);
    }

    [Fact]
    public void Flows_after_until_are_ignored()
    {
        // D12: the replay stopped at 31 December of the previous year.
        var r = Replay(
            [Pay(2018, 1, 1, 100_000m), Pay(2025, 12, 31, 5_000m), Take(2026, 3, 1, 50_000m), Pay(2026, 4, 1, 7_000m)],
            [Value(2026, 2, 28, 150_000m)],
            until: new LocalDate(2025, 12, 31));

        Assert.Equal(105_000m, r.After2017);
        Assert.False(r.Approximate);
    }

    [Fact]
    public void A_neutral_transfer_out_reports_the_capital_it_carries()
    {
        // 1st: 50 000 x 100 000 / 125 000 = 40 000 -> 60 000; 2nd: 20 000 x 60 000 / 80 000 = 15 000 -> 45 000
        var r = Replay(
            [
                Pay(2018, 1, 1, 100_000m),
                new(new LocalDate(2021, 1, 15), CapitalFlowKind.NeutralTransferOut, 20_000m),
                new(new LocalDate(2020, 7, 1), CapitalFlowKind.NeutralTransferOut, 50_000m),
            ],
            [Value(2020, 6, 30, 125_000m), Value(2021, 1, 1, 80_000m)]);

        Assert.Equal(45_000m, r.After2017);
        Assert.Equal(new[] { (0m, 40_000m), (0m, 15_000m) }, r.TransferredOut);
    }

    [Fact]
    public void A_neutral_transfer_in_adds_the_capital_it_carries_not_its_amount()
    {
        // D16: capital 100 000 + 40 000 = 140 000; V = 125 000 + 50 000 = 175 000; 35 000 x 140 000 / 175 000 = 28 000 -> 112 000
        var r = Replay(
            [
                Pay(2018, 1, 1, 100_000m),
                new(new LocalDate(2020, 1, 1), CapitalFlowKind.NeutralTransferIn, 50_000m, TransferredAfter2017: 40_000m),
                Take(2020, 1, 2, 35_000m),
            ],
            [Value(2019, 12, 31, 125_000m)]);

        Assert.Equal(112_000m, r.After2017);
        Assert.Empty(r.TransferredOut);
    }

    [Fact]
    public void A_neutral_transfer_keeps_the_dates_of_its_premiums()
    {
        // Source: opening 100 000, 60 000 of them pre-2017; 50 000 x 100 000 / 125 000 = 40 % leaves -> (24 000, 16 000) carried
        var source = Replay(
            [Pay(2016, 1, 1, 100_000m), new(new LocalDate(2020, 7, 1), CapitalFlowKind.NeutralTransferOut, 50_000m)],
            [Value(2020, 6, 30, 125_000m)],
            before2017AtStart: 60_000m);
        var (carriedBefore, carriedAfter) = Assert.Single(source.TransferredOut);

        // Destination: its first capital is the transfer, so premiumsBefore2017AtStart is ignored and the 2021 payment is post-2017.
        var destination = Replay(
            [
                new(new LocalDate(2020, 7, 1), CapitalFlowKind.NeutralTransferIn, 50_000m, carriedBefore, carriedAfter),
                Pay(2021, 1, 1, 5_000m),
            ],
            [],
            before2017AtStart: 10_000m);

        Assert.Equal((36_000m, 24_000m), (source.Before2017, source.After2017));
        Assert.Equal((24_000m, 16_000m), (carriedBefore, carriedAfter));
        Assert.Equal((24_000m, 21_000m), (destination.Before2017, destination.After2017));
        Assert.False(destination.Approximate);
    }

    [Fact]
    public void A_neutral_transfer_in_without_its_split_is_post_2017_and_approximate()
    {
        var r = Replay([new(new LocalDate(2016, 1, 1), CapitalFlowKind.NeutralTransferIn, 50_000m)], []);

        Assert.Equal((0m, 50_000m), (r.Before2017, r.After2017));
        Assert.True(r.Approximate);
    }
}
