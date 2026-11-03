using Nid.Core.Contracts;
using Nid.RealEstate.Contracts;
using Nid.RealEstate.Domain;
using NodaTime;

namespace Nid.RealEstate.Tests;

/// <summary>The months still due (arrears follow-up): the ledger's months with a remainder, overdue past the grace, and their lease chain.</summary>
public class RentArrearsTests
{
    private static readonly ItemId Home = ItemId.New();

    private static Flow Cash(LocalDate on, decimal amount)
        => new(Guid.NewGuid(), Home, on, FlowKind.Income, Loans.Ok(Money.Create(amount, Currency.Eur)), PropertyCategories.Rent, null, null);

    private static Tenancy Lease(LocalDate starts, LocalDate? ends = null, decimal rent = 800m, decimal charges = 50m)
        => new("T3", starts, ends, rent, charges, null, null);

    private static IReadOnlyList<(Guid Id, Guid? RevisedFrom, Tenancy Tenancy)> One(Tenancy t) => [(Guid.NewGuid(), null, t)];

    private static readonly Tenancy Standard = Lease(new LocalDate(2025, 1, 1));

    private static List<Flow> Paid(LocalDate from, int count, decimal amount = 850m)
        => Enumerable.Range(0, count).Select(m => Cash(from.PlusMonths(m), amount)).ToList();

    [Fact]
    public void Lists_only_months_with_a_remainder_oldest_first()
    {
        var today = new LocalDate(2026, 4, 30);
        // Paid from May 2025 to Jan 2026, then nothing: Feb, Mar, Apr remain.
        var months = RentArrears.Of(One(Standard), Paid(new LocalDate(2025, 5, 5), 9), today);
        Assert.Equal([new LocalDate(2026, 2, 1), new LocalDate(2026, 3, 1), new LocalDate(2026, 4, 1)], months.Select(m => m.Month));
        Assert.All(months, m => Assert.True(m.Remaining > 0));
        Assert.Empty(RentArrears.Of(One(Standard), Paid(new LocalDate(2025, 5, 5), 12), today));
    }

    public static TheoryData<int> Scenarios => [0, 1, 2, 3];

    private static (IReadOnlyList<(Guid, Guid?, Tenancy)> Tenancies, List<Flow> Flows) Scenario(int scenario)
    {
        var on5 = new LocalDate(2024, 11, 5);
        return scenario switch
        {
            0 => (One(Lease(new LocalDate(2020, 9, 1), rent: 900m, charges: 0m)), Paid(on5, 8, 900m)),
            // partial payment
            1 => (One(Standard), [.. Paid(on5, 5), Cash(new LocalDate(2025, 4, 5), 300m)]),
            // paid ahead: a credit beyond the overdue dues
            3 => (One(Standard), Paid(on5, 12, 2000m)),
            // revision mid-month
            _ => (RevisedPair(new LocalDate(2025, 6, 16), 809.22m).Tenancies, Paid(on5.PlusMonths(4), 3)),
        };
    }

    private static (IReadOnlyList<(Guid Id, Guid? RevisedFrom, Tenancy Tenancy)> Tenancies, Guid Old, Guid Revised) RevisedPair(LocalDate on, decimal rent)
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        return ([(a, null, Lease(new LocalDate(2020, 9, 1), on.PlusDays(-1))), (b, a, Lease(on, rent: rent))], a, b);
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Remainders_sum_to_the_arrears(int scenario)
    {
        var today = new LocalDate(2025, 10, 15);
        var (tenancies, flows) = Scenario(scenario);
        var plain = tenancies.Select(t => t.Item3).ToList();
        var arrears = RentLedger.Arrears(plain, flows, today);
        Assert.Equal(Math.Max(0m, arrears), RentArrears.Of(tenancies, flows, today).Sum(m => m.Remaining));
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Overdue_remainders_sum_to_the_watch_amount(int scenario)
    {
        var today = new LocalDate(2025, 10, 15);
        var (tenancies, flows) = Scenario(scenario);
        var plain = tenancies.Select(t => t.Item3).ToList();
        var watch = RentLedger.ArrearsDueBy(plain, flows, today, RentArrears.GraceDays);
        Assert.Equal(Math.Max(0m, watch), RentArrears.Of(tenancies, flows, today).Where(m => m.Overdue).Sum(m => m.Remaining));
    }

    [Fact]
    public void A_payer_of_the_5th_has_nothing_overdue_on_the_23rd()
    {
        var flows = Paid(new LocalDate(2025, 11, 5), 12); // Nov 2025 .. Oct 2026
        Assert.Empty(RentArrears.Of(One(Standard), flows, new LocalDate(2026, 10, 23)));
        // On 1 November the new month is due and unpaid: listed, « en cours ».
        var months = RentArrears.Of(One(Standard), flows, new LocalDate(2026, 11, 1));
        var november = Assert.Single(months);
        Assert.Equal(new LocalDate(2026, 11, 1), november.Month);
        Assert.False(november.Overdue);
    }

    [Fact]
    public void A_month_is_overdue_from_the_11th_of_the_next_month()
    {
        var flows = Paid(new LocalDate(2025, 11, 5), 7); // Nov 2025 .. May 2026; June unpaid
        Assert.False(RentArrears.Of(One(Standard), flows, new LocalDate(2026, 7, 10)).Single(m => m.Month == new LocalDate(2026, 6, 1)).Overdue);
        Assert.True(RentArrears.Of(One(Standard), flows, new LocalDate(2026, 7, 11)).Single(m => m.Month == new LocalDate(2026, 6, 1)).Overdue);
    }

    [Fact]
    public void Each_month_carries_its_chain()
    {
        Guid oldTenant = Guid.NewGuid(), oldRevision = Guid.NewGuid(), newTenant = Guid.NewGuid();
        var switchOn = new LocalDate(2026, 6, 15);
        IReadOnlyList<(Guid, Guid?, Tenancy)> tenancies =
        [
            (oldTenant, null, Lease(new LocalDate(2025, 1, 1), new LocalDate(2026, 2, 28))),
            (oldRevision, oldTenant, Lease(new LocalDate(2026, 3, 1), switchOn.PlusDays(-1), rent: 820m)),
            (newTenant, null, Lease(switchOn)),
        ];
        var months = RentArrears.Of(tenancies, Paid(new LocalDate(2025, 7, 5), 8), new LocalDate(2026, 6, 20)); // Jul 2025 .. Feb 2026 paid
        Assert.Equal(oldRevision, months.Single(m => m.Month == new LocalDate(2026, 3, 1)).Chain); // the chain's last link
        Assert.Equal(oldRevision, months.Single(m => m.Month == new LocalDate(2026, 5, 1)).Chain);
        // The changeover month is one row per tenant: each owes its own days.
        Assert.Equal([oldRevision, newTenant], months.Where(m => m.Month == new LocalDate(2026, 6, 1)).Select(m => m.Chain));
    }

    // One account per tenant: Mme Martin (850) leaves on 15 June 2026 owing May; M. Durand (900) moves in that day.
    private static readonly LocalDate SwitchOn = new(2026, 6, 15), October23 = new(2026, 10, 23);
    private static readonly Guid Martin = Guid.NewGuid(), Durand = Guid.NewGuid();
    private static readonly Tenancy MartinLease = Lease(new LocalDate(2025, 1, 1), SwitchOn.PlusDays(-1)), DurandLease = Lease(SwitchOn, rent: 850m);
    private static readonly IReadOnlyList<(Guid Id, Guid? RevisedFrom, Tenancy Tenancy)> TwoTenants = [(Martin, null, MartinLease), (Durand, null, DurandLease)];
    private static readonly decimal MartinJune = PropertyMath.Expected(MartinLease, new LocalDate(2026, 6, 1), new LocalDate(2026, 6, 14));
    private static readonly decimal DurandJune = PropertyMath.Expected(DurandLease, SwitchOn, new LocalDate(2026, 6, 30));

    /// <summary>Mme Martin paid January 2025 to April 2026 on the 5th; M. Durand pays his June days on moving in, then 900 on the 5th.</summary>
    private static List<Flow> MartinOwesMay(bool durandPaysJune = true)
        => [.. Paid(new LocalDate(2025, 1, 5), 16), .. durandPaysJune ? [Cash(SwitchOn, DurandJune)] : Array.Empty<Flow>(), .. Paid(new LocalDate(2026, 7, 5), 4, 900m)];

    [Fact]
    public void A_new_tenant_s_rent_never_settles_the_former_tenant_s_debt()
    {
        var flows = MartinOwesMay();
        var months = RentArrears.Of(TwoTenants, flows, October23);

        Assert.Equal([(new LocalDate(2026, 5, 1), 850m, Martin), (new LocalDate(2026, 6, 1), MartinJune, Martin)], months.Select(m => (m.Month, m.Remaining, m.Chain!.Value)));
        Assert.All(months, m => Assert.True(m.Overdue));
        Assert.Equal(850m + MartinJune, RentLedger.PropertyArrears(TwoTenants, flows, October23));
        Assert.Equal(850m + MartinJune, RentLedger.PropertyArrearsDueBy(TwoTenants, flows, October23, RentArrears.GraceDays));

        // M. Durand's receipts read his own account: every month of his is a quittance.
        var durand = RentLedger.Accounts(TwoTenants, flows).Single(a => a.Chain == Durand);
        for (var month = new LocalDate(2026, 7, 1); month <= new LocalDate(2026, 10, 1); month = month.PlusMonths(1))
            Assert.Equal("quittance", RentReceipt.For(durand.Tenancies, durand.Cash, month, October23)!.Kind);
        Assert.Equal(DurandJune, RentLedger.Months(durand.Tenancies, durand.Cash, October23)[0].Imputed);
    }

    [Fact]
    public void The_changeover_month_is_split_by_days_between_the_two_tenants()
    {
        var accounts = RentLedger.Accounts(TwoTenants, MartinOwesMay(durandPaysJune: false));
        var june = accounts.Select(a => RentLedger.Months(a.Tenancies, a.Cash, October23).Single(m => m.Month == new LocalDate(2026, 6, 1)).Due);
        Assert.Equal([MartinJune, DurandJune], june);
        // M. Durand's July rent settles his own June days first, so his last month is the one short.
        var durand = RentArrears.Of(TwoTenants, MartinOwesMay(durandPaysJune: false), October23).Single(m => m.Chain == Durand);
        Assert.Equal((new LocalDate(2026, 10, 1), DurandJune), (durand.Month, durand.Remaining));
    }

    [Fact]
    public void Both_halves_of_the_changeover_month_are_listed_when_neither_is_paid()
    {
        var months = RentArrears.Of(TwoTenants, Paid(new LocalDate(2025, 1, 5), 17), new LocalDate(2026, 6, 20)); // Martin paid through May
        Assert.Equal([(Martin, MartinJune, MartinJune), (Durand, DurandJune, DurandJune)],
            months.Select(m => (m.Chain!.Value, m.Due, m.Remaining)));
        Assert.All(months, m => Assert.Equal(new LocalDate(2026, 6, 1), m.Month));
    }

    [Fact]
    public void Cash_goes_to_the_tenant_whose_lease_covers_its_date()
    {
        var gone = Lease(new LocalDate(2025, 1, 1), new LocalDate(2026, 4, 30));
        var next = Lease(new LocalDate(2026, 6, 1));
        IReadOnlyList<(Guid, Guid?, Tenancy)> tenancies = [(Martin, null, gone), (Durand, null, next)];
        Flow before = Cash(new LocalDate(2024, 12, 20), 1m), during = Cash(new LocalDate(2026, 4, 5), 2m), gap = Cash(new LocalDate(2026, 5, 10), 3m),
            after = Cash(new LocalDate(2026, 6, 5), 4m);
        var accounts = RentLedger.Accounts(tenancies, [after, gap, during, before]);
        Assert.Equal([Martin, Durand], accounts.Select(a => a.Chain));
        // Before any lease: the first tenant; in the gap between two leases: the one that ended.
        Assert.Equal([before, during, gap], accounts[0].Cash.OrderBy(f => f.OccurredOn));
        Assert.Equal([after], accounts[1].Cash);

        // After the last lease ended: its tenant.
        var ended = RentLedger.Accounts([(Martin, null, gone)], [Cash(new LocalDate(2026, 9, 1), 5m)]);
        Assert.Single(Assert.Single(ended).Cash);
    }

    [Fact]
    public void A_former_tenant_s_late_payment_counts_for_the_tenant_in_place_a_known_limit()
    {
        // Mme Martin settles May on 2 July, once M. Durand has moved in: a flow does not say who paid, its date does.
        var flows = MartinOwesMay();
        flows.Add(Cash(new LocalDate(2026, 7, 2), 850m));
        var months = RentArrears.Of(TwoTenants, flows, October23);
        Assert.Equal(850m, months.Single(m => m.Month == new LocalDate(2026, 5, 1)).Remaining);
        Assert.Equal(-850m, RentLedger.Arrears(
            RentLedger.Accounts(TwoTenants, flows).Single(a => a.Chain == Durand).Tenancies,
            RentLedger.Accounts(TwoTenants, flows).Single(a => a.Chain == Durand).Cash, October23)); // a credit on M. Durand's account
    }

    /// <summary>Mme Martin until 31 May 2026, her rents never recorded in Nid; M. Durand from 1 June pays 900 on the 5th.</summary>
    private static (IReadOnlyList<(Guid Id, Guid? RevisedFrom, Tenancy Tenancy)> Tenancies, List<Flow> Flows) RecordedFromTheNewTenant(LocalDate? trackedFrom = null)
        => ([(Martin, null, Lease(new LocalDate(2020, 1, 1), new LocalDate(2026, 5, 31)) with { RentsTrackedFrom = trackedFrom }),
             (Durand, null, Lease(new LocalDate(2026, 6, 1), rent: 850m))], Paid(new LocalDate(2026, 6, 5), 5, 900m));

    [Fact]
    public void A_former_tenant_without_recorded_rents_owes_nothing()
    {
        var (tenancies, flows) = RecordedFromTheNewTenant();
        Assert.Equal(0m, RentLedger.PropertyArrears(tenancies, flows, October23));
        Assert.Equal(0m, RentLedger.PropertyArrearsDueBy(tenancies, flows, October23, RentArrears.GraceDays));
        Assert.Empty(RentArrears.Of(tenancies, flows, October23)); // no row, so no letter
        var martin = RentLedger.Accounts(tenancies, flows).Single(a => a.Chain == Martin);
        Assert.Null(RentReceipt.For(martin.Tenancies, martin.Cash, new LocalDate(2026, 5, 1), October23));
    }

    [Fact]
    public void A_former_tenant_with_a_month_set_owes_from_it()
    {
        var (tenancies, flows) = RecordedFromTheNewTenant(new LocalDate(2026, 3, 1));
        Assert.Equal([(new LocalDate(2026, 3, 1), Martin), (new LocalDate(2026, 4, 1), Martin), (new LocalDate(2026, 5, 1), Martin)],
            RentArrears.Of(tenancies, flows, October23).Select(m => (m.Month, m.Chain!.Value)));
        Assert.Equal(3 * 850m, RentLedger.PropertyArrears(tenancies, flows, October23));
    }

    /// <summary>Mme Martin until 31 May 2026, paid January 2025 to May 2026; M. Durand from 1 June pays June on <paramref name="advance"/>, then 900 on the 5th.</summary>
    private static (IReadOnlyList<(Guid Id, Guid? RevisedFrom, Tenancy Tenancy)> Tenancies, List<Flow> Flows) PaidAtSigning(LocalDate advance)
        => ([(Martin, null, Lease(new LocalDate(2025, 1, 1), new LocalDate(2026, 5, 31))), (Durand, null, Lease(new LocalDate(2026, 6, 1), rent: 850m))],
            [.. Paid(new LocalDate(2025, 1, 5), 17), Cash(advance, 900m), .. Paid(new LocalDate(2026, 7, 5), 4, 900m)]);

    [Fact]
    public void A_payment_made_within_15_days_before_moving_in_is_the_incoming_tenant_s()
    {
        var (tenancies, flows) = PaidAtSigning(new LocalDate(2026, 5, 28));
        var durand = RentLedger.Accounts(tenancies, flows).Single(a => a.Chain == Durand);
        for (var month = new LocalDate(2026, 6, 1); month <= new LocalDate(2026, 10, 1); month = month.PlusMonths(1))
            Assert.Equal("quittance", RentReceipt.For(durand.Tenancies, durand.Cash, month, October23)!.Kind);
        Assert.Empty(RentArrears.Of(tenancies, flows, October23)); // nothing owed by either, the current month included
        Assert.Equal(0m, RentLedger.PropertyArrears(tenancies, flows, October23));

        // From start − 15 days, inclusive.
        Assert.Contains(RentLedger.Accounts(tenancies, [Cash(new LocalDate(2026, 5, 17), 1m)]).Single(a => a.Chain == Durand).Cash, f => f.Amount.Amount == 1m);
        Assert.Empty(RentLedger.Accounts(tenancies, [Cash(new LocalDate(2026, 5, 16), 1m)]).Single(a => a.Chain == Durand).Cash);
    }

    [Fact]
    public void A_payment_made_earlier_than_15_days_before_moving_in_stays_the_outgoing_tenant_s()
    {
        var (tenancies, flows) = PaidAtSigning(new LocalDate(2026, 5, 10));
        Assert.Contains(RentLedger.Accounts(tenancies, flows).Single(a => a.Chain == Martin).Cash, f => f.OccurredOn == new LocalDate(2026, 5, 10));
        // M. Durand's October is short by the June he paid before; Mme Martin's account is in credit.
        var row = Assert.Single(RentArrears.Of(tenancies, flows, October23));
        Assert.Equal((new LocalDate(2026, 10, 1), Durand, 900m), (row.Month, row.Chain!.Value, row.Remaining));
    }

    [Fact]
    public void A_current_tenant_without_recorded_rents_still_owes_the_last_12_months()
    {
        Assert.Equal(12, RentArrears.Of(One(Standard), [], new LocalDate(2026, 10, 31)).Count);
    }

    [Fact]
    public void A_former_tenant_s_credit_never_hides_the_current_tenant_s_debt()
    {
        // Mme Martin overpaid by 1 000; M. Durand has not paid October.
        var flows = MartinOwesMay();
        flows.RemoveAt(flows.Count - 1); // M. Durand's October
        flows.Add(Cash(new LocalDate(2026, 5, 25), 850m + MartinJune + 1000m)); // before M. Durand's 15 days
        Assert.Equal(900m, RentLedger.PropertyArrears(TwoTenants, flows, October23));
        Assert.Equal([(new LocalDate(2026, 10, 1), Durand)], RentArrears.Of(TwoTenants, flows, October23).Select(m => (m.Month, m.Chain!.Value)));

        // Every account at zero or in credit: the signed total, « plus perçu que prévu ».
        flows.Add(Cash(new LocalDate(2026, 10, 5), 900m));
        Assert.Equal(-1000m, RentLedger.PropertyArrears(TwoTenants, flows, October23));
        Assert.Equal(0m, RentLedger.PropertyArrearsDueBy(TwoTenants, flows, October23, RentArrears.GraceDays));
    }

    [Fact]
    public void A_cyclic_revision_chain_still_ends()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var months = RentArrears.Of([(a, b, Lease(new LocalDate(2025, 1, 1), new LocalDate(2025, 5, 31))), (b, a, Lease(new LocalDate(2025, 6, 1)))], [], new LocalDate(2025, 8, 20));
        Assert.NotEmpty(months);
    }

    [Fact]
    public void A_revision_chain_reads_oldest_first_whatever_order_it_comes_in()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        Tenancy old = Lease(new LocalDate(2025, 1, 1), new LocalDate(2025, 12, 31)), revised = Lease(new LocalDate(2026, 1, 1));
        Assert.Equal([old, revised], Assert.Single(RentLedger.Accounts([(b, a, revised), (a, null, old)], [])).Tenancies);
    }

    [Fact]
    public void A_payment_on_a_lease_s_first_day_within_the_next_tenant_s_15_days_is_the_next_tenant_s()
    {
        var on = new LocalDate(2026, 6, 1);
        IReadOnlyList<(Guid, Guid?, Tenancy)> tenancies = [(Martin, null, Lease(on)), (Durand, null, Lease(on.PlusDays(9)))];
        Assert.Single(RentLedger.Accounts(tenancies, [Cash(on, 1m)]).Single(a => a.Chain == Durand).Cash);
    }

    [Fact]
    public void Two_leases_covering_a_date_give_it_to_the_latest_started_even_when_revised()
    {
        Guid revised = Guid.NewGuid();
        IReadOnlyList<(Guid, Guid?, Tenancy)> tenancies =
        [
            (Martin, null, Lease(new LocalDate(2025, 1, 1))),
            (Durand, null, Lease(new LocalDate(2025, 3, 1), new LocalDate(2025, 5, 31))),
            (revised, Durand, Lease(new LocalDate(2025, 6, 1))),
        ];
        Assert.Single(RentLedger.Accounts(tenancies, [Cash(new LocalDate(2025, 7, 5), 1m)]).Single(a => a.Chain == revised).Cash);
    }

    [Fact]
    public void After_every_lease_ended_the_cash_goes_to_the_one_that_ended_last()
    {
        IReadOnlyList<(Guid, Guid?, Tenancy)> tenancies =
            [(Martin, null, Lease(new LocalDate(2020, 1, 1), new LocalDate(2020, 12, 31))), (Durand, null, Lease(new LocalDate(2022, 1, 1), new LocalDate(2022, 12, 31)))];
        Assert.Single(RentLedger.Accounts(tenancies, [Cash(new LocalDate(2024, 1, 5), 1m)]).Single(a => a.Chain == Durand).Cash);
    }

    [Fact]
    public void A_chain_ends_with_its_last_link_not_its_first()
    {
        Guid revised = Guid.NewGuid();
        IReadOnlyList<(Guid, Guid?, Tenancy)> tenancies =
        [
            (Martin, null, Lease(new LocalDate(2020, 1, 1), new LocalDate(2020, 12, 31))),
            (revised, Martin, Lease(new LocalDate(2021, 1, 1), new LocalDate(2022, 12, 31))),
            (Durand, null, Lease(new LocalDate(2021, 6, 1), new LocalDate(2021, 12, 31))),
        ];
        Assert.Single(RentLedger.Accounts(tenancies, [Cash(new LocalDate(2024, 1, 5), 1m)]).Single(a => a.Chain == revised).Cash);
    }

    [Fact]
    public void Without_any_lease_the_cash_is_a_credit()
        => Assert.Equal(-100m, RentLedger.PropertyArrears([], [Cash(October23, 100m)], October23));
}
