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

/// <summary>An arrangement in full: every revision, oldest first, its weeks without care, those still to place this contract year, and the paid-leave days accrued (decision 16).</summary>
public sealed record ChildcareDetail(
    ChildcareSummary Summary, string? CarerPajemploi, string? CarerApproval, IReadOnlyList<ChildcareTermsView> Terms, IReadOnlyList<CareGapView> Gaps,
    int GapWeeksToPlace, int LeaveDaysAccrued, uint Version, ChildcareMoneyView? CmgEstimate = null) : IExposesItems
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
    string? TemplateDeparture, bool Changed);

/// <summary>Spec §3: the Pajemploi fields, hours as <c>"1.50"</c>, amounts as <c>"598.02"</c>.</summary>
public sealed record DeclarationView(
    int NormalHours, int ActivityDays, int? PaidLeaveDays, string ComplementaryHours, string OvertimeHours, string NetPay, string Upkeep, string Meals,
    string Mileage);

/// <summary>The declaration with the pay's parts and the month's cost; <c>CostBeforeCmg</c> while the CMG is not entered (decision 17).</summary>
public sealed record StatementFiguresView(
    DeclarationView Declaration, ChildcareMoneyView BasePay, ChildcareMoneyView Deduction, ChildcareMoneyView ExtraPay, ChildcareMoneyView PaidLeavePay,
    ChildcareMoneyView Cost, bool CostBeforeCmg);

public sealed record FieldProblemView(string Field, string Code);

/// <summary>
/// Spec §5 « Relevé du mois ». <c>Figures</c> null while a day is inconsistent (<c>Problems</c>) or, in June, a month of the
/// reference period is not validated (<c>MissingReferenceMonths</c>). <c>Version</c> 0: nothing stored yet. <c>CmgEstimate</c>: decision 19.
/// </summary>
public sealed record ChildcareStatementView(
    Guid Id, string Name, string Month, string State, IReadOnlyList<StatementDayView> Days, string Mileage, string? Cmg, StatementFiguresView? Figures,
    IReadOnlyList<FieldProblemView> Problems, IReadOnlyList<string> MissingReferenceMonths, bool CanWrite, bool CanReopen, uint Version,
    string? CmgEstimate = null) : IExposesItems
{
    public IEnumerable<ItemId> ExposedItems() => [new ItemId(Id)];
}
