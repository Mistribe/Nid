using System.Globalization;
using Nid.Childcare.Contracts;
using Nid.Childcare.Domain;
using Nid.Core.Contracts;
using NodaTime;
using NodaTime.Text;

namespace Nid.Childcare.Application;

/// <summary>Spec §5: the arrangements' views, their figures computed by the Domain.</summary>
public sealed class ChildcareReader(IChildcareStore store, IPersonRegistry persons, IHouseholdSettings household, IHouseholdCalendar calendar, IAccessPolicy policy)
{
    internal static readonly LocalTimePattern Time = LocalTimePattern.CreateWithInvariantCulture("HH:mm");

    public async Task<IReadOnlyList<ChildcareDetail>> ReadAsync(PersonId reader, IReadOnlyList<(Arrangement Arrangement, ItemAccess Item)> found, CancellationToken ct)
    {
        var ids = found.Select(f => f.Arrangement.ItemId).ToList();
        var terms = (await store.TermsOfAsync(ids, ct)).ToLookup(t => t.ItemId);
        var gaps = (await store.GapsOfAsync(ids, ct)).ToLookup(g => g.Item, g => g.Gap);
        var overrides = (await store.OverridesOfAsync(ids, ct)).ToLookup(o => o.Item, o => o.Override);
        var writable = await policy.CheckManyAsync(reader, ids, AccessKind.Write, ct);
        var names = (await persons.ListAsync(ct)).ToDictionary(p => p.Id, p => p.DisplayName);
        var currency = await household.ReferenceCurrencyAsync(ct);
        var today = await calendar.TodayAsync(ct);
        var estimates = new Dictionary<ItemId, decimal?>();
        var validated = new Dictionary<ItemId, HashSet<YearMonth>>();
        var sickDeducted = new Dictionary<ItemId, IReadOnlyList<(YearMonth Month, int Deducted)>>();
        var leaveTaken = new Dictionary<ItemId, IReadOnlyList<(YearMonth Month, int Taken)>>();
        foreach (var id in ids)
        {
            var statements = (await store.StatementsOfAsync(id, ct)).Where(st => st.Validated).ToList();
            estimates[id] = StatementContext.CmgEstimate(statements.Where(st => st.Figures is not null).Select(st => (st.Month, st.Figures!.Cmg)).ToList());
            validated[id] = statements.Select(st => st.Month).ToHashSet();
            sickDeducted[id] = statements.Where(st => st.Figures is not null).Select(st => (st.Month, st.Figures!.ChildSickDeducted)).ToList();
            leaveTaken[id] = statements.Where(st => st.Figures is not null).Select(st => (st.Month, st.Figures!.PaidLeaveTaken)).ToList();
        }

        return found.Select(f =>
        {
            var (arrangement, item) = f;
            var revisions = terms[arrangement.ItemId].ToList();
            var views = revisions.Select(t => TermsView(t, currency)).ToList();
            var inForce = Math.Max(0, revisions.FindLastIndex(t => t.From <= today));
            var (now, current) = (revisions[inForce], views[inForce]);
            var summary = new ChildcareSummary(
                arrangement.ItemId.Value, item.DisplayName, arrangement.Child.Value, names.GetValueOrDefault(arrangement.Child, ""), arrangement.CarerName,
                item.OwnerViews(), item.Visibility == Visibility.Private ? "private" : "shared", item.ArchivedAt is not null,
                writable.Contains(arrangement.ItemId), revisions[0].From, views[^1].To, current.From, current.MonthlyBasePay,
                Hours(Mensualisation.AnnualHours(now.WeekTypes) / now.ProgrammedWeeks),
                StatementContext.ToPrepare(revisions[0].From, views[^1].To, today, validated[arrangement.ItemId]).Select(StatementAssembly.Format).ToList());
            var placed = gaps[arrangement.ItemId].ToList();
            var year = ContractCheck.Year(TermsPlan.Of(now, revisions[0].From, overrides[arrangement.ItemId].ToList()), revisions[0].From, placed, today);
            var allocated = ContractCheck.Allocated(year, now.WeekTypes.Count);
            return new ChildcareDetail(summary, arrangement.CarerPajemploi, arrangement.CarerApproval, views,
                placed.Select(g => new CareGapView(g.Monday, g.Weeks, g.Monday.PlusWeeks(g.Weeks).PlusDays(-1))).ToList(),
                ContractCheck.GapWeeksToPlace(revisions[0].From, revisions[^1].ProgrammedWeeks, placed, today),
                StatementContext.LeaveDaysAccrued(revisions[0].From, revisions[^1].To, today), arrangement.Version,
                year.Select(w => new ContractWeekView(w.Monday, w.WeekType, w.Overridden, w.Gap)).ToList(),
                now.WeekTypes.Select((w, i) => new WeekTypeCountView(w.Name, allocated[i], w.Weeks)).ToList(),
                GapYears(revisions, views[^1].To, placed, today),
                estimates[arrangement.ItemId] is { } cmg ? ChildcareMoneyView.Of(cmg, currency) : null,
                StatementContext.ChildSickUsed(revisions[0].From, today, sickDeducted[arrangement.ItemId]),
                Leave: now.FullYear ? Leave(revisions[0].From, views[^1].To, today.ToYearMonth(), leaveTaken[arrangement.ItemId]) : null);
        }).ToList();
    }

    private static List<GapYearView> GapYears(List<Terms> revisions, LocalDate? end, List<CareGap> placed, LocalDate today)
    {
        var gaps = placed.Select(g => new CareGapView(g.Monday, g.Weeks, g.Monday.PlusWeeks(g.Weeks).PlusDays(-1))).ToList();
        return ContractCheck.GapYears(revisions[0].From, end, d => revisions.LastOrDefault(t => t.From <= d, revisions[0]).ProgrammedWeeks, placed, today)
            .Select(y => new GapYearView(y.From, y.To, y.Allowed, y.Placed, Math.Max(0, y.Allowed - y.Placed),
                gaps.Where(g => g.Monday >= y.From && g.Monday <= y.To).ToList()))
            .ToList();
    }

    /// <summary>The list's order: open first, then by child and name.</summary>
    public static IReadOnlyList<ChildcareSummary> Ordered(IEnumerable<ChildcareSummary> items)
        => items.OrderBy(i => i.Closed).ThenBy(i => i.ChildName, StringComparer.Create(new CultureInfo("fr-FR"), true))
            .ThenBy(i => i.Name, StringComparer.Create(new CultureInfo("fr-FR"), true)).ToList();

    private static LeaveCounterView Leave(LocalDate start, LocalDate? end, YearMonth month, IReadOnlyList<(YearMonth Month, int Taken)> taken)
    {
        var left = StatementContext.LeaveLeft(start, end, month, taken);
        return new LeaveCounterView(StatementContext.LeaveAccruedBefore(start, end, month), taken.Where(t => t.Month.CompareTo(month) < 0).Sum(t => t.Taken), left);
    }

    private static ChildcareTermsView TermsView(Terms t, Currency currency)
    {
        var annual = Mensualisation.AnnualHours(t.WeekTypes);
        return new ChildcareTermsView(t.From, t.To, Text(t.NetHourlyRate), t.GrossHourlyRate is { } g ? Text(g) : null, Text(t.OvertimeRate * 100m),
            Text(t.UpkeepPer9h), t.LunchPrice is { } m ? Text(m) : null, t.SnackPrice is { } sp ? Text(sp) : null,
            MealProviderCodes.Of(t.LunchProvider), MealProviderCodes.Of(t.SnackProvider),
            t.WeekTypes.Select(w => new WeekTypeView(w.Name, w.Weeks,
                w.Schedule.Select(s => new WeekdaySlotView((int)s.Day, Time.Format(s.Arrival), Time.Format(s.Departure), s.Lunch, s.Snack)).ToList(),
                Hours(Mensualisation.WeeklyHours(w.Schedule)))).ToList(),
            t.ProgrammedWeeks, ChildcareMoneyView.Of(Mensualisation.BasePay(t.NetHourlyRate, annual), currency), Hours(annual / 12m), Mensualisation.NormalHours(annual),
            (t.UnpaidAbsences ?? AbsenceCodes.DefaultUnpaid).Select(AbsenceCodes.Of).ToList(), t.FullYear);
    }

    private static string Hours(decimal hours) => hours.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>An entered figure, exact, without trailing zeros.</summary>
    internal static string Text(decimal value) => (value / 1.000000000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);
}
