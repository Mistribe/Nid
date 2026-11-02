using System.Globalization;
using Nid.Core.Contracts;
using NodaTime;

namespace Nid.Childcare.Application;

/// <summary>An amount on the wire, computed and rounded to the cent server-side.</summary>
public sealed record ChildcareMoneyView(string Amount, string Currency)
{
    public static ChildcareMoneyView Of(decimal amount, Currency currency)
        => new(Math.Round(amount, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture), currency.Code);
}

/// <summary>
/// Spec §5: an arrangement in a list. <c>MonthlyBasePay</c>, <c>WeeklyHours</c>: the terms in force today, which start on <c>InForceFrom</c>
/// (the first terms before the contract starts; a revision dated after today waits). <c>End</c>: the last terms' end.
/// </summary>
public sealed record ChildcareSummary(
    Guid Id, string Name, Guid ChildId, string ChildName, string CarerName, IReadOnlyList<ItemOwnerView> Owners, string Visibility, bool Closed,
    bool CanWrite, LocalDate Start, LocalDate? End, LocalDate InForceFrom, ChildcareMoneyView MonthlyBasePay, string WeeklyHours);

public sealed record ChildcareList(IReadOnlyList<ChildcareSummary> Items) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => Items.Select(i => new ItemId(i.Id));
}

/// <summary>One day of the template: ISO day (1 = Monday), times as <c>HH:mm</c>.</summary>
public sealed record WeekdaySlotView(int Day, string Arrival, string Departure, bool Meal);

/// <summary>One revision with its figures; rates as entered (decimal strings), <c>OvertimePct</c> in percent, the base pay computed.</summary>
public sealed record ChildcareTermsView(
    LocalDate From, LocalDate? To, string NetHourlyRate, string? GrossHourlyRate, string OvertimePct, string UpkeepPer9h, string? MealPrice,
    int ProgrammedWeeks, IReadOnlyList<WeekdaySlotView> Schedule, ChildcareMoneyView MonthlyBasePay, string WeeklyHours, int NormalHours);

public sealed record CareGapView(LocalDate Monday, int Weeks, LocalDate Sunday);

/// <summary>An arrangement in full: every revision, oldest first, its weeks without care and those still to place this contract year.</summary>
public sealed record ChildcareDetail(
    ChildcareSummary Summary, string? CarerPajemploi, string? CarerApproval, IReadOnlyList<ChildcareTermsView> Terms, IReadOnlyList<CareGapView> Gaps,
    int GapWeeksToPlace, uint Version) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => [new ItemId(Summary.Id)];
}
