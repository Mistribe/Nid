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
        var writable = await policy.CheckManyAsync(reader, ids, AccessKind.Write, ct);
        var names = (await persons.ListAsync(ct)).ToDictionary(p => p.Id, p => p.DisplayName);
        var currency = await household.ReferenceCurrencyAsync(ct);
        var today = await calendar.TodayAsync(ct);

        return found.Select(f =>
        {
            var (arrangement, item) = f;
            var revisions = terms[arrangement.ItemId].ToList();
            var views = revisions.Select(t => TermsView(t, currency)).ToList();
            var current = views.LastOrDefault(v => v.From <= today) ?? views[0];
            var summary = new ChildcareSummary(
                arrangement.ItemId.Value, item.DisplayName, arrangement.Child.Value, names.GetValueOrDefault(arrangement.Child, ""), arrangement.CarerName,
                item.OwnerViews(), item.Visibility == Visibility.Private ? "private" : "shared", item.ArchivedAt is not null,
                writable.Contains(arrangement.ItemId), revisions[0].From, views[^1].To, current.From, current.MonthlyBasePay, current.WeeklyHours);
            var placed = gaps[arrangement.ItemId].ToList();
            return new ChildcareDetail(summary, arrangement.CarerPajemploi, arrangement.CarerApproval, views,
                placed.Select(g => new CareGapView(g.Monday, g.Weeks, g.Monday.PlusWeeks(g.Weeks).PlusDays(-1))).ToList(),
                ContractCheck.GapWeeksToPlace(revisions[0].From, revisions[^1].ProgrammedWeeks, placed, today),
                StatementContext.LeaveDaysAccrued(revisions[0].From, revisions[^1].To, today), arrangement.Version);
        }).ToList();
    }

    /// <summary>The list's order: open first, then by child and name.</summary>
    public static IReadOnlyList<ChildcareSummary> Ordered(IEnumerable<ChildcareSummary> items)
        => items.OrderBy(i => i.Closed).ThenBy(i => i.ChildName, StringComparer.Create(new CultureInfo("fr-FR"), true))
            .ThenBy(i => i.Name, StringComparer.Create(new CultureInfo("fr-FR"), true)).ToList();

    private static ChildcareTermsView TermsView(Terms t, Currency currency)
    {
        var hours = Mensualisation.WeeklyHours(t.Schedule);
        return new ChildcareTermsView(t.From, t.To, Text(t.NetHourlyRate), t.GrossHourlyRate is { } g ? Text(g) : null, Text(t.OvertimeRate * 100m),
            Text(t.UpkeepPer9h), t.MealPrice is { } m ? Text(m) : null, t.ProgrammedWeeks,
            t.Schedule.Select(s => new WeekdaySlotView((int)s.Day, Time.Format(s.Arrival), Time.Format(s.Departure), s.Meal)).ToList(),
            ChildcareMoneyView.Of(Mensualisation.BasePay(t.NetHourlyRate, hours, t.ProgrammedWeeks), currency),
            hours.ToString("0.00", CultureInfo.InvariantCulture), Mensualisation.NormalHours(hours, t.ProgrammedWeeks));
    }

    /// <summary>An entered figure, exact, without trailing zeros.</summary>
    internal static string Text(decimal value) => (value / 1.000000000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);
}
