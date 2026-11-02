using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;

namespace Nid.RealEstate.Tests;

/// <summary>Rent receipts: all the cash imputed to the months due, oldest first, and the receipt figures of a month.</summary>
public class RentLedgerTests
{
    private static readonly ItemId Home = ItemId.New();
    private static readonly LocalDate Today = new(2026, 4, 30);

    private static Money Eur(decimal amount) => Loans.Ok(Money.Create(amount, Currency.Eur));

    private static Flow Cash(LocalDate on, decimal amount, string category = PropertyCategories.Rent)
        => new(Guid.NewGuid(), Home, on, FlowKind.Income, Eur(amount), category, null, null);

    private static Tenancy Lease(LocalDate starts, LocalDate? ends = null, decimal rent = 800m, decimal charges = 50m)
        => new("T3", starts, ends, rent, charges, null, null);

    private static readonly Tenancy Standard = Lease(new LocalDate(2025, 1, 1));

    /// <summary>850 on the 5th of each month from May 2025 to April 2026, but the months in <paramref name="skip"/>.</summary>
    private static List<Flow> Year(params (int Year, int Month)[] skip)
        => Enumerable.Range(0, 12).Select(m => new LocalDate(2025, 5, 5).PlusMonths(m))
            .Where(on => !skip.Contains((on.Year, on.Month))).Select(on => Cash(on, 850m)).ToList();

    private static LedgerMonth Of(IReadOnlyList<LedgerMonth> months, int year, int month) => months.Single(m => m.Month == new LocalDate(year, month, 1));

    [Fact]
    public void Imputes_oldest_first()
    {
        // March's rent comes on 10 April with April's.
        var flows = Year((2026, 3), (2026, 4));
        flows.Add(Cash(new LocalDate(2026, 4, 10), 1700m));
        var months = RentLedger.Months([Standard], flows, Today);
        Assert.Equal(12, months.Count);
        Assert.All(months, m => Assert.Equal(0m, m.Remaining));
        Assert.Equal(new LocalDate(2026, 4, 10), Of(months, 2026, 3).LastPaidOn);
        Assert.Equal(new LocalDate(2026, 4, 10), Of(months, 2026, 4).LastPaidOn);
        Assert.Equal(new LocalDate(2026, 2, 5), Of(months, 2026, 2).LastPaidOn);
    }

    [Fact]
    public void A_shortfall_stays_on_the_latest_months()
    {
        var months = RentLedger.Months([Standard], Year((2026, 1)), Today);
        Assert.Equal(850m, Of(months, 2026, 4).Remaining);
        Assert.Equal(0m, Of(months, 2026, 4).Imputed);
        Assert.All(months.Where(m => m.Month < new LocalDate(2026, 4, 1)), m => Assert.Equal(0m, m.Remaining));
    }

    [Fact]
    public void Old_rent_with_charges_is_cash_like_any_other()
    {
        var months = RentLedger.Months([Standard], Year(), Today); // single `rent` flows of 850
        Assert.All(months, m => Assert.Equal(0m, m.Remaining));
    }

    private static readonly PropertyDetails Bought
        = new(PropertyType.Rental, new LocalDate(2020, 6, 15), 250_000m, 19_000m, 0m, 0m, null, null, PropertyCadence.Yearly, null, null, null);

    public static TheoryData<int> Scenarios => [0, 1, 2, 3];

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Remainders_sum_to_the_arrears(int scenario)
    {
        var d = new LocalDate(2025, 10, 15);
        var on5 = Enumerable.Range(0, 12).Select(m => new LocalDate(2024, 11, 5).PlusMonths(m)).ToList();
        var (tenancies, flows) = scenario switch
        {
            // partial first window month (16 Oct 2024), 8 payments
            0 => (new[] { Lease(new LocalDate(2020, 9, 1), rent: 900m, charges: 0m) }, on5.Take(8).Select(on => Cash(on, 900m)).ToList()),
            // starts mid-window, one lump
            1 => (new[] { Lease(new LocalDate(2025, 3, 10)) }, [Cash(new LocalDate(2025, 6, 1), 2000m)]),
            // revision mid-month
            2 => (new[] { Lease(new LocalDate(2020, 9, 1), new LocalDate(2025, 6, 16)), Lease(new LocalDate(2025, 6, 16), rent: 809.22m) },
                on5.Skip(4).Take(3).Select(on => Cash(on, 850m)).ToList()),
            // overpaid: arrears negative, remainders all 0
            _ => (new[] { Standard }, on5.Select(on => Cash(on, 2000m)).ToList()),
        };
        var arrears = PropertyMath.At(Bought, true, [], flows, [], Loans.Chain(tenancies), d).Rental.Arrears;
        var months = RentLedger.Months(tenancies, flows, d);
        Assert.Equal(Math.Max(0m, arrears), months.Sum(m => m.Remaining));
    }

    private static ReceiptFigures? Receipt(IEnumerable<Flow> flows, int year, int month, params Tenancy[] tenancies)
        => RentReceipt.For(tenancies.Length == 0 ? [Standard] : tenancies, flows.ToList(), new LocalDate(year, month, 1), Today);

    [Fact]
    public void Receipt_is_a_quittance_when_the_month_is_settled()
    {
        var r = Receipt(Year(), 2026, 2)!;
        Assert.Equal("quittance", r.Kind);
        Assert.Equal((800m, 50m, 850m, 850m), (r.Rent, r.Charges, r.Total, r.Paid));
        Assert.Equal(new LocalDate(2026, 2, 5), r.PaidOn);
    }

    [Fact]
    public void Receipt_is_a_receipt_for_a_partial_payment()
    {
        // The only cash of W is 600, which the oldest months absorb first: ask for the first month.
        var r = RentReceipt.For([Standard], [Cash(new LocalDate(2025, 5, 7), 600m)], new LocalDate(2025, 5, 1), Today)!;
        Assert.Equal("receipt", r.Kind);
        Assert.Equal(600m, r.Paid);
        Assert.Equal(new LocalDate(2025, 5, 7), r.PaidOn);
    }

    [Fact]
    public void Unpaid_month_is_unpaid()
    {
        var r = RentReceipt.For([Standard], [], new LocalDate(2026, 4, 1), Today)!;
        Assert.Equal("unpaid", r.Kind);
        Assert.Equal(0m, r.Paid);
        Assert.Equal(850m, r.Total);
        Assert.Null(r.PaidOn);
    }

    [Fact]
    public void Current_month_uses_the_months_end()
    {
        var today = new LocalDate(2026, 10, 3);
        // At the month's end (2026-10-31) W starts on 2025-11-01: November 2025 to September 2026 paid.
        var earlier = Enumerable.Range(0, 11).Select(m => Cash(new LocalDate(2025, 11, 5).PlusMonths(m), 850m)).ToList();
        Assert.Equal("quittance", RentReceipt.For([Standard], [.. earlier, Cash(today, 850m)], new LocalDate(2026, 10, 1), today)!.Kind);
        var r = RentReceipt.For([Standard], [.. earlier, Cash(today, 680m)], new LocalDate(2026, 10, 1), today)!;
        Assert.Equal("receipt", r.Kind);
        Assert.Equal(680m, r.Paid);
    }

    [Fact]
    public void Revision_month_sums_both_links()
    {
        var old = Lease(new LocalDate(2025, 1, 1), new LocalDate(2026, 9, 16), 800m, 0m);
        var revised = Lease(new LocalDate(2026, 9, 16), rent: 809.22m, charges: 0m);
        var r = RentReceipt.For([old, revised], [], new LocalDate(2026, 9, 1), new LocalDate(2026, 9, 30))!;
        var expected = PropertyMath.Expected(old, new LocalDate(2026, 9, 1), new LocalDate(2026, 9, 15))
            + PropertyMath.Expected(revised, new LocalDate(2026, 9, 16), new LocalDate(2026, 9, 30));
        Assert.Equal(expected, r.Rent);
        Assert.Equal(expected, r.Total);
        Assert.Equal(0m, r.Charges);
    }

    [Fact]
    public void Month_outside_the_window_or_in_the_future_is_null()
    {
        Assert.Null(Receipt([], 2026, 5)); // after today's month
        Assert.Null(Receipt([], 2025, 4)); // before W (starts 2025-05-01)
        Assert.NotNull(Receipt([], 2025, 5));
    }

    [Fact]
    public void Month_without_tenancy_is_null()
        => Assert.Null(Receipt([], 2026, 2, Lease(new LocalDate(2026, 3, 1))));

    // The ledger counts each tenancy from its tracked start T, to the end of d's month, over all the cash.
    private static readonly LocalDate October23 = new(2026, 10, 23);
    private static readonly Tenancy Since2018 = Lease(new LocalDate(2018, 1, 1));
    private static readonly Tenancy SinceMarch = Lease(new LocalDate(2025, 3, 1));

    /// <summary>850 on <paramref name="first"/> and the same day of each month, <paramref name="count"/> times.</summary>
    private static List<Flow> Monthly(LocalDate first, int count) => Enumerable.Range(0, count).Select(m => Cash(first.PlusMonths(m), 850m)).ToList();

    private static decimal Arrears(IReadOnlyList<Flow> flows, LocalDate d, params Tenancy[] tenancies)
        => PropertyMath.At(Bought, true, [], flows, [], Loans.Chain(tenancies), d).Rental.Arrears;

    [Fact]
    public void A_lease_older_than_its_recorded_rents_counts_from_the_first_one_or_from_the_month_set()
    {
        var flows = Monthly(new LocalDate(2025, 3, 5), 20); // March 2025 to October 2026
        Assert.Equal(new LocalDate(2025, 3, 1), RentLedger.Tracked(Since2018, flows, October23));
        Assert.Equal(0m, Arrears(flows, October23, Since2018));
        Assert.Equal("quittance", RentReceipt.For([Since2018], flows, new LocalDate(2025, 3, 1), October23)!.Kind);
        Assert.Null(RentReceipt.For([Since2018], flows, new LocalDate(2025, 2, 1), October23));

        // « Loyers suivis depuis » February 2025: February is due, and oldest first the shortfall lands on October.
        var set = Since2018 with { RentsTrackedFrom = new LocalDate(2025, 2, 1) };
        Assert.Equal(new LocalDate(2025, 2, 1), RentLedger.Tracked(set, flows, October23));
        Assert.Equal(850m, Arrears(flows, October23, set));
        Assert.Equal("quittance", RentReceipt.For([set], flows, new LocalDate(2025, 2, 1), October23)!.Kind);
        Assert.Equal("unpaid", RentReceipt.For([set], flows, new LocalDate(2026, 10, 1), October23)!.Kind);
    }

    [Fact]
    public void An_advance_payer_counts_from_the_lease_start_and_the_current_month_is_a_quittance()
    {
        // The 28th for the next month, from 28 February for March, the lease's first month.
        var flows = Monthly(new LocalDate(2025, 2, 28), 20);
        Assert.Equal(SinceMarch.StartsOn, RentLedger.Tracked(SinceMarch, flows, October23));
        Assert.Equal(0m, Arrears(flows, October23, SinceMarch));
        Assert.Equal("quittance", RentReceipt.For([SinceMarch], flows, new LocalDate(2026, 10, 1), October23)!.Kind);
    }

    [Fact]
    public void A_late_first_payment_counts_from_the_lease_start_and_the_current_month_is_unpaid_until_paid()
    {
        // March paid on 2 April, then each month on the 2nd of the next: March 2025 to September 2026.
        var flows = Monthly(new LocalDate(2025, 4, 2), 19);
        Assert.Equal(SinceMarch.StartsOn, RentLedger.Tracked(SinceMarch, flows, October23));
        var march = RentReceipt.For([SinceMarch], flows, new LocalDate(2025, 3, 1), October23)!;
        Assert.Equal(("quittance", new LocalDate(2025, 4, 2)), (march.Kind, march.PaidOn));
        Assert.Equal("quittance", RentReceipt.For([SinceMarch], flows, new LocalDate(2026, 9, 1), October23)!.Kind);
        Assert.Equal("unpaid", RentReceipt.For([SinceMarch], flows, new LocalDate(2026, 10, 1), October23)!.Kind);
        Assert.Equal(850m, Arrears(flows, October23, SinceMarch)); // October is due whole from the 1st

        List<Flow> paid = [.. flows, Cash(October23, 850m)];
        Assert.Equal("quittance", RentReceipt.For([SinceMarch], paid, new LocalDate(2026, 10, 1), October23)!.Kind);
        Assert.Equal(0m, Arrears(paid, October23, SinceMarch));
    }

    [Fact]
    public void A_first_rent_after_the_lease_s_second_month_starts_the_count_in_its_month()
    {
        // The lease starts in March; the first rent recorded is dated 5 May: the rents before were kept elsewhere.
        var flows = Monthly(new LocalDate(2025, 5, 5), 18);
        Assert.Equal(new LocalDate(2025, 5, 1), RentLedger.Tracked(SinceMarch, flows, October23));
        Assert.Equal(0m, Arrears(flows, October23, SinceMarch));
        // By the end of April, the lease's second month, it would have counted from the start.
        Assert.Equal(SinceMarch.StartsOn, RentLedger.Tracked(SinceMarch, [Cash(new LocalDate(2025, 4, 30), 850m)], October23));
    }

    [Fact]
    public void Without_any_cash_in_the_arrears_are_the_last_12_months_and_the_current_one_whole()
    {
        Assert.Equal(new LocalDate(2025, 10, 24), RentLedger.Tracked(Since2018, [], October23));
        var rental = PropertyMath.At(Bought, true, [], [], [], Loans.Chain([Since2018]), October23).Rental;
        Assert.Equal(PropertyMath.Expected(Since2018, new LocalDate(2025, 10, 24), new LocalDate(2026, 10, 31)), rental.Arrears);
        Assert.Equal(rental.ExpectedRent + 850m * 8 / 31, rental.Arrears, 2); // the 8 days of October still to come
        Assert.Null(RentReceipt.For([Since2018], [], new LocalDate(2025, 9, 1), October23));
        Assert.Equal("unpaid", RentReceipt.For([Since2018], [], new LocalDate(2025, 10, 1), October23)!.Kind);
    }

    [Fact]
    public void Each_tenancy_counts_from_its_own_start()
    {
        // A first tenant until June 2025, never recorded, then a second one from July whose rents are all in Nid.
        var gone = Lease(new LocalDate(2020, 1, 1), new LocalDate(2025, 7, 1));
        var next = Lease(new LocalDate(2025, 7, 1));
        var flows = Monthly(new LocalDate(2025, 7, 5), 16); // July 2025 to October 2026
        Assert.Equal(0m, Arrears(flows, October23, gone, next));
        var months = RentLedger.Months([gone, next], flows, October23);
        Assert.Equal(new LocalDate(2025, 7, 1), months[0].Month);
        Assert.Null(RentReceipt.For([gone, next], flows, new LocalDate(2025, 6, 1), October23));
    }

    [Fact]
    public void A_lease_starting_later_this_month_owes_nothing_before_its_start()
    {
        var soon = Lease(new LocalDate(2026, 10, 25));
        Assert.Equal(0m, Arrears([], October23, soon));
        Assert.Empty(RentLedger.Months([soon], [], October23));
        Assert.Null(RentReceipt.For([soon], [], new LocalDate(2026, 10, 1), October23));
        Assert.Equal(191.94m, Arrears([], new LocalDate(2026, 10, 25), soon)); // from its start: 850 × 7 ÷ 31
    }

    [Fact]
    public void The_watch_leaves_a_payer_of_the_5th_alone()
    {
        var flows = Monthly(new LocalDate(2025, 3, 5), 20); // March 2025 to October 2026
        Assert.True(RentLedger.ArrearsDueBy([SinceMarch], flows, October23, 10) <= 0m);
        Assert.True(RentLedger.ArrearsDueBy([SinceMarch], flows, new LocalDate(2026, 11, 5), 10) <= 0m); // November not yet recorded
    }

    [Fact]
    public void The_watch_flags_an_unpaid_month_from_the_11th_of_the_next()
    {
        var flows = Monthly(new LocalDate(2025, 3, 5), 19); // October 2026 unpaid
        Assert.True(RentLedger.ArrearsDueBy([SinceMarch], flows, new LocalDate(2026, 11, 10), 10) <= 0m);
        Assert.Equal(850m, RentLedger.ArrearsDueBy([SinceMarch], flows, new LocalDate(2026, 11, 11), 10));
        // The figure itself owes October from its 1st.
        Assert.Equal(850m, Arrears(flows, October23, SinceMarch));
    }

    [Fact]
    public void The_watch_never_flags_a_routine_user()
    {
        // The routine records each month's rent on the 3rd of the next, dated the month's last day.
        for (var d = new LocalDate(2025, 3, 1); d <= new LocalDate(2026, 12, 31); d = d.PlusDays(1))
        {
            var day = d;
            var flows = Enumerable.Range(0, 30).Select(m => new LocalDate(2025, 3, 1).PlusMonths(m))
                .Where(month => month.PlusMonths(1).PlusDays(2) <= day)
                .Select(month => Cash(month.PlusMonths(1).PlusDays(-1), 850m)).ToList();
            Assert.True(RentLedger.ArrearsDueBy([SinceMarch], flows, day, 10) <= 0m, $"flagged on {day}");
        }
    }

    [Fact]
    public void Cash_before_the_lease_start_counts()
    {
        // 1 700 paid on the signature, a week before the lease starts: the first two months are settled.
        var months = RentLedger.Months([SinceMarch], [Cash(new LocalDate(2025, 2, 22), 1700m)], new LocalDate(2025, 4, 30));
        Assert.Equal(new[] { 0m, 0m }, months.Select(m => m.Remaining));
        Assert.Equal(new LocalDate(2025, 3, 1), months[0].Month);
    }

    private static readonly LocalDate Fallback = new(2025, 10, 24); // October23 − 12 months + 1 day

    /// <summary>Money that moves on the property without being a tenant's cash.</summary>
    public static TheoryData<FlowKind, string> NotCash => new()
    {
        { FlowKind.Income, "other_income" },
        { FlowKind.Expense, PropertyCategories.Rent },
    };

    private static Flow Flow(LocalDate on, FlowKind kind, string category) => new(Guid.NewGuid(), Home, on, kind, Eur(100m), category, null, null);

    [Fact]
    public void A_payment_on_the_lease_s_first_day_counts_from_the_lease_start()
        => Assert.Equal(SinceMarch.StartsOn, RentLedger.Tracked(SinceMarch, [Cash(SinceMarch.StartsOn, 850m)], October23));

    [Fact]
    public void A_payment_on_the_evaluation_day_is_a_first_rent()
        => Assert.Equal(new LocalDate(2026, 10, 1), RentLedger.Tracked(Since2018, [Cash(October23, 850m)], October23));

    [Fact]
    public void A_first_rent_on_the_day_after_the_lease_s_second_month_starts_the_count_in_its_month()
        => Assert.Equal(new LocalDate(2025, 5, 1), RentLedger.Tracked(SinceMarch, [Cash(new LocalDate(2025, 5, 1), 850m)], October23));

    [Fact]
    public void Cash_before_the_lease_start_is_no_first_rent()
        => Assert.Equal(Fallback, RentLedger.Tracked(SinceMarch, [Cash(new LocalDate(2025, 2, 22), 850m)], October23));

    [Theory]
    [MemberData(nameof(NotCash))]
    public void A_flow_that_is_not_a_tenant_s_cash_is_no_first_rent(FlowKind kind, string category)
        => Assert.Equal(Fallback, RentLedger.Tracked(SinceMarch, [Flow(new LocalDate(2025, 3, 5), kind, category)], October23));

    [Theory]
    [MemberData(nameof(NotCash))]
    public void A_flow_that_is_not_a_tenant_s_cash_settles_nothing(FlowKind kind, string category)
    {
        List<Flow> flows = [Flow(new LocalDate(2026, 4, 5), kind, category)];
        Assert.Equal(RentLedger.Arrears([Standard], [], Today), RentLedger.Arrears([Standard], flows, Today));
        Assert.All(RentLedger.Months([Standard], flows, Today), m => Assert.Equal(0m, m.Imputed));
    }

    [Fact]
    public void The_tracked_start_is_never_before_the_lease()
    {
        Assert.Equal(SinceMarch.StartsOn, RentLedger.Tracked(SinceMarch with { RentsTrackedFrom = new LocalDate(2025, 1, 1) }, [], October23));
        Assert.Equal(SinceMarch.StartsOn, RentLedger.Tracked(SinceMarch, [], new LocalDate(2025, 4, 30))); // the 12 months reach back before it
    }

    [Fact]
    public void Without_any_tenancy_the_ledger_starts_12_months_back()
        => Assert.Equal(Fallback, RentLedger.Start([], [], October23));

    [Fact]
    public void A_lease_ended_before_the_month_owes_none_of_it_whatever_its_tracked_start()
    {
        var gone = Lease(new LocalDate(2025, 1, 1), new LocalDate(2025, 3, 1));
        Assert.False(RentLedger.Counts(gone, gone.StartsOn, new LocalDate(2025, 4, 1)));
    }

    [Theory]
    [MemberData(nameof(NotCash))]
    public void A_former_tenant_with_only_other_flows_owes_nothing(FlowKind kind, string category)
    {
        var gone = Lease(new LocalDate(2020, 1, 1), new LocalDate(2026, 1, 1));
        Assert.Empty(RentLedger.Started([gone], [Flow(new LocalDate(2025, 6, 5), kind, category)], October23));
    }

    [Fact]
    public void A_former_tenant_s_payment_on_the_evaluation_day_is_recorded_cash()
    {
        var gone = Lease(new LocalDate(2020, 1, 1), new LocalDate(2026, 1, 1));
        Assert.Single(RentLedger.Started([gone], [Cash(October23, 850m)], October23));
    }

    [Fact]
    public void A_payment_on_the_evaluation_day_is_imputed()
    {
        var month = Assert.Single(RentLedger.Months([Standard], [Cash(Today, 850m)], Today));
        Assert.Equal((850m, Today), (month.Imputed, month.LastPaidOn));
    }

    [Fact]
    public void A_tracked_start_inside_a_month_prorates_that_month()
    {
        var first = RentLedger.Months([Since2018], [], October23)[0];
        Assert.Equal((new LocalDate(2025, 10, 1), 219.35m), (first.Month, first.Due)); // 850 × 8 ÷ 31, from the 24th
    }

    [Fact]
    public void A_month_with_nothing_due_is_never_paid_nor_a_quittance()
    {
        var free = Lease(new LocalDate(2025, 1, 1), rent: 0m, charges: 0m);
        List<Flow> flows = [Cash(new LocalDate(2026, 4, 5), 10m)];
        var month = Assert.Single(RentLedger.Months([free], flows, Today));
        Assert.Equal((0m, 0m, (LocalDate?)null), (month.Due, month.Imputed, month.LastPaidOn));
        Assert.Equal("unpaid", RentReceipt.For([free], flows, new LocalDate(2026, 4, 1), Today)!.Kind);
    }

    private static readonly LocalDate October1 = new(2026, 10, 1);
    private static readonly Tenancy TrackedFromOctober = Standard with { RentsTrackedFrom = October1 };

    [Fact]
    public void A_receipt_for_the_current_month_reads_the_cash_to_the_month_s_last_day()
        => Assert.Equal("quittance", RentReceipt.For([Standard], [Cash(new LocalDate(2026, 10, 20), 850m)], October1, new LocalDate(2026, 10, 3))!.Kind);

    [Fact]
    public void A_receipt_for_the_current_month_stops_at_the_month_s_last_day()
        => Assert.Equal("unpaid", RentReceipt.For([TrackedFromOctober], [Cash(new LocalDate(2026, 11, 1), 850m)], October1, new LocalDate(2026, 10, 3))!.Kind);

    [Fact]
    public void A_receipt_for_a_past_month_reads_the_cash_to_today()
    {
        var november1 = new LocalDate(2026, 11, 1);
        Assert.Equal("quittance", RentReceipt.For([TrackedFromOctober], [Cash(november1, 850m)], October1, november1)!.Kind);
    }

    [Fact]
    public void A_month_can_be_asked_for_from_its_first_day()
    {
        var april1 = new LocalDate(2026, 4, 1);
        Assert.NotNull(RentReceipt.For([Standard], [], april1, april1));
        Assert.True(RentReceipt.InRange([Standard], [], april1, april1));
    }

    [Fact]
    public void A_month_after_today_or_that_no_tenancy_owes_is_out_of_range()
    {
        Assert.False(RentReceipt.InRange([Standard], [], new LocalDate(2026, 5, 1), Today));
        Assert.False(RentReceipt.InRange([Standard], [], new LocalDate(2025, 4, 1), Today)); // before the last 12 months
        Assert.True(RentReceipt.InRange([Standard], [], new LocalDate(2026, 4, 15), Today)); // read as its month
    }
}
