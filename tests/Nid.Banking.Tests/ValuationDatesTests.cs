using Nid.Banking.Domain;
using NodaTime;

namespace Nid.Banking.Tests;

public class ValuationDatesTests
{
    private static LocalDate D(int month, int day) => new(2026, month, day);

    private static readonly LocalDate[] Balances = [D(8, 20), D(8, 31), D(9, 15)];

    [Fact]
    public void Nightly_values_each_elapsed_month_end_with_the_latest_balance_of_its_month()
        => Assert.Equal([(D(8, 31), D(8, 31)), (D(9, 30), D(9, 15))], ValuationDates.Due(D(8, 12), null, Balances, D(10, 2), manual: false));

    [Fact]
    public void Month_ends_already_done_are_left_out()
        => Assert.Equal([(D(9, 30), D(9, 15))], ValuationDates.Due(D(8, 12), D(8, 31), Balances, D(10, 2), manual: false));

    [Fact]
    public void A_manual_sync_adds_today_with_the_latest_balance()
        => Assert.Equal([(D(8, 31), D(8, 31)), (D(9, 30), D(9, 15)), (D(10, 2), D(9, 15))],
            ValuationDates.Due(D(8, 12), null, Balances, D(10, 2), manual: true));

    [Fact]
    public void A_second_click_the_same_day_leaves_today_as_the_first_left_it()
        => Assert.Empty(ValuationDates.Due(D(8, 12), D(10, 2), Balances, D(10, 2), manual: true));

    [Fact]
    public void A_month_without_a_balance_is_skipped()
        => Assert.Equal([(D(9, 30), D(9, 15))], ValuationDates.Due(D(7, 1), null, [D(9, 15)], D(10, 2), manual: false));

    [Fact]
    public void The_association_month_before_its_first_balance_yields_nothing()
        => Assert.Empty(ValuationDates.Due(D(8, 12), null, [D(9, 3)], D(9, 5), manual: false));

    [Fact]
    public void Without_any_balance_a_manual_sync_values_nothing()
        => Assert.Empty(ValuationDates.Due(D(8, 12), null, [], D(10, 2), manual: true));
}
