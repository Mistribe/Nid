using Nid.Core.Application.Dashboard;
using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Core.Application.Tax;

/// <summary>
/// Issue #409: the household's dates of the yearly tax calendar (<see cref="TaxCalendar"/>) in [<c>today</c>, <c>until</c>], about no
/// item, leading to the tax page: the filing deadline of the profile's department (unknown: the earliest, labelled
/// <see cref="DepartmentUnknown"/>), the new withholding rate, and the balance with the amount of last year's notice when it prints
/// one (a notice with nothing to pay has no balance event). The property tax is RealEstate's, per property.
/// </summary>
public sealed class TaxCalendarEvents(ITaxProfileStore profiles, ITaxAssessmentStore assessments, IPersonRegistry persons)
{
    public const string DepartmentUnknown = "department_unknown";

    public async Task<IReadOnlyList<DashboardEventView>> EventsAsync(
        PersonId reader, VisibilityScope scope, LocalDate today, LocalDate until, CancellationToken ct)
    {
        bool Within(LocalDate on) => on >= today && on <= until;
        DashboardEventView Event(LocalDate on, string kind, string? label = null, decimal? amount = null)
            => new(on, kind, null, "tax", "", label, amount is { } a ? DashboardMath.Money(a, Currency.Eur) : null);

        var events = new List<DashboardEventView>();
        for (var year = today.Year; year <= until.Year; year++)
        {
            var calendar = TaxCalendar.For(year);
            if (Within(calendar.FilingNorth) || Within(calendar.FilingCentre) || Within(calendar.FilingSouth))
            {
                // May's filing is of the revenues of year − 1, the year a profile is keyed by; this year's when that one has none.
                var department = (await profiles.FindAsync(year - 1, ct))?.Profile.Department ?? (await profiles.FindAsync(year, ct))?.Profile.Department;
                var deadline = calendar.FilingDeadline(department);
                if (Within(deadline ?? calendar.FilingNorth))
                    events.Add(Event(deadline ?? calendar.FilingNorth, UpcomingKinds.TaxFilingDeadline, deadline is null ? DepartmentUnknown : null));
            }
            if (Within(calendar.NewRate))
                events.Add(Event(calendar.NewRate, UpcomingKinds.TaxNewRate));
            if (Within(calendar.Balance))
            {
                var people = await persons.ListAsync(ct);
                var balance = (await assessments.ListForAsync(reader, year - 1, ct))
                    .Where(n => scope != VisibilityScope.Household || TaxAssessmentAccess.InHousehold(n.Assessment, people))
                    .Select(n => n.Assessment.Balance).FirstOrDefault(b => b is not null);
                // The September debit is a date of the calendar: without a notice (or a balance on it) it comes alone, without an
                // amount; only a notice with nothing to pay drops it.
                if (balance is not <= 0m)
                    events.Add(Event(calendar.Balance, UpcomingKinds.TaxBalanceDue, amount: balance));
            }
        }
        return events;
    }
}
