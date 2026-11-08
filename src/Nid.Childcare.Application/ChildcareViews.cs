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

/// <summary>One template week: its name, weeks in the contract, slots and hours per week (<c>"39.00"</c>).</summary>
public sealed record WeekTypeView(string Name, int Weeks, IReadOnlyList<WeekdaySlotView> Schedule, string WeeklyHours);

/// <summary>
/// One revision with its figures; rates as entered (decimal strings), <c>OvertimePct</c> in percent, the base pay and
/// <c>MonthlyHours</c> (annual hours over 12, <c>"143.96"</c>) computed.
/// </summary>
public sealed record ChildcareTermsView(
    LocalDate From, LocalDate? To, string NetHourlyRate, string? GrossHourlyRate, string OvertimePct, string UpkeepPer9h, string? MealPrice,
    IReadOnlyList<WeekTypeView> WeekTypes, int ProgrammedWeeks, ChildcareMoneyView MonthlyBasePay, string MonthlyHours, int NormalHours);

public sealed record CareGapView(LocalDate Monday, int Weeks, LocalDate Sunday);

/// <summary>A week of the contract year: its Monday, the rank of its week type, whether an exception sets it, whether care is off.</summary>
public sealed record ContractWeekView(LocalDate Monday, int WeekType, bool Overridden, bool Gap);

/// <summary>The weeks with care allocated to a week type against the weeks it has in the contract (a mark, not a limit).</summary>
public sealed record WeekTypeCountView(string Name, int Allocated, int Contract);

/// <summary>
/// An arrangement in full: every revision, oldest first, its weeks without care, those still to place this contract year, and the paid-leave days accrued (decision 16).
/// <c>Weeks</c> and <c>Allocation</c>: the contract year in progress, under the revision in force.
/// </summary>
public sealed record ChildcareDetail(
    ChildcareSummary Summary, string? CarerPajemploi, string? CarerApproval, IReadOnlyList<ChildcareTermsView> Terms, IReadOnlyList<CareGapView> Gaps,
    int GapWeeksToPlace, int LeaveDaysAccrued, uint Version, IReadOnlyList<ContractWeekView> Weeks, IReadOnlyList<WeekTypeCountView> Allocation,
    ChildcareMoneyView? CmgEstimate = null) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => [new ItemId(Summary.Id)];
}

/// <summary>Decision 13: a month of the arrangement. <c>State</c>: <c>none</c>, <c>draft</c> or <c>validated</c>; <c>Cost</c> once validated.</summary>
public sealed record ChildcareStatementRow(string Month, string State, ChildcareMoneyView? Cost);

/// <summary>Every month a statement exists for, newest first.</summary>
public sealed record ChildcareStatementList(Guid Id, IReadOnlyList<ChildcareStatementRow> Months) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => [new ItemId(Id)];
}

/// <summary>A day of the statement; times <c>HH:mm</c>, the template's beside them; <c>Changed</c>: differs from the template's day.</summary>
public sealed record StatementDayView(
    LocalDate Date, bool Planned, bool Present, string? Arrival, string? Departure, bool Meal, string? Absence, string? TemplateArrival,
    string? TemplateDeparture, bool Changed, string? WeekType = null);

/// <summary>Spec §3: the Pajemploi fields, hours as <c>"1.50"</c>, amounts as <c>"612.50"</c>.</summary>
public sealed record DeclarationView(
    int NormalHours, int ActivityDays, int? PaidLeaveDays, string ComplementaryHours, string OvertimeHours, string NetPay, string Upkeep, string Meals,
    string Mileage);

/// <summary>The declaration with the pay's parts and the month's cost; <c>CostBeforeCmg</c> while the CMG is not entered (decision 17).</summary>
public sealed record StatementFiguresView(
    DeclarationView Declaration, ChildcareMoneyView BasePay, ChildcareMoneyView Deduction, ChildcareMoneyView ExtraPay, ChildcareMoneyView PaidLeavePay,
    ChildcareMoneyView Cost, bool CostBeforeCmg);

public sealed record FieldProblemView(string Field, string Code);

/// <summary>Decision 30: the declaration's fields entered by hand, null where the month's own stands.</summary>
public sealed record DeclaredView(
    int? NormalHours, int? ActivityDays, int? PaidLeaveDays, string? ComplementaryHours, string? OvertimeHours, string? NetPay, string? Upkeep, string? Meals);

/// <summary>
/// Spec §5 « Relevé du mois ». <c>Figures</c> null while a day is inconsistent (<c>Problems</c>) or, in June, a month of the
/// reference period is not validated (<c>MissingReferenceMonths</c>). <c>Version</c> 0: nothing stored yet. <c>CmgEstimate</c>: decision 19. <c>Declared</c>: decision 30.
/// <c>Computed</c>: #32, the declaration as Nid computes it without the fields entered by hand, beside <c>Figures</c>' (the one the month uses).
/// </summary>
public sealed record ChildcareStatementView(
    Guid Id, string Name, string Month, string State, IReadOnlyList<StatementDayView> Days, string Mileage, string? Cmg, StatementFiguresView? Figures,
    IReadOnlyList<FieldProblemView> Problems, IReadOnlyList<string> MissingReferenceMonths, bool CanWrite, bool CanReopen, uint Version,
    string? CmgEstimate = null, DeclaredView? Declared = null, DeclarationView? Computed = null) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => [new ItemId(Id)];
}

/// <summary>
/// #411 « Récapitulatif {année} »: the year's validated months summed, as the tax estimate counts them. <c>Retained</c> is the
/// « reste à charge retenu pour le crédit d'impôt » (net pay + upkeep − CMG), to write in <c>Box</c> (7GA, 7GB or 7GC; null past the
/// third child). <c>NotValidated</c>: the year's months, <c>yyyy-MM</c>, left out because they are not validated. <c>Years</c>: those the
/// arrangement has months in, newest first.
/// </summary>
public sealed record ChildcareYearView(
    Guid Id, string Name, string ChildName, string CarerName, int Year, IReadOnlyList<int> Years, string? Box, ChildcareMoneyView NetPay,
    ChildcareMoneyView Cmg, ChildcareMoneyView Upkeep, ChildcareMoneyView Meals, ChildcareMoneyView Retained, IReadOnlyList<string> ValidatedMonths,
    IReadOnlyList<string> NotValidated) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => [new ItemId(Id)];
}
