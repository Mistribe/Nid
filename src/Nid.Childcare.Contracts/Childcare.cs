using NodaTime;

namespace Nid.Childcare.Contracts;

/// <summary>Childcare spec §2: why a planned day was not worked; decides whether the salary is deducted.</summary>
public enum AbsenceReason { Family, ParentRtt, ChildSick, CarerAbsent, Holiday }

/// <summary>One day of the weekly template: the child's arrival and departure, and whether the carer gives the meal.</summary>
public sealed record WeekdaySlot(IsoDayOfWeek Day, LocalTime Arrival, LocalTime Departure, bool Meal);

/// <summary>A template week and the number of weeks of the year it covers; the rotation follows the list order.</summary>
public sealed record WeekType(string Name, int Weeks, IReadOnlyList<WeekdaySlot> Schedule);

/// <summary>The week from this Monday follows the given week type (index) instead of the rotation.</summary>
public sealed record WeekOverride(LocalDate Monday, int WeekType);

/// <summary>Whole weeks without care, from a Monday.</summary>
public sealed record CareGap(LocalDate Monday, int Weeks);

/// <summary>The contract's figures the month needs (spec §2), all in net.</summary>
public sealed record ContractTerms(LocalDate Start, LocalDate? End, decimal NetHourlyRate, decimal OvertimeRate, decimal UpkeepPer9h, decimal? MealPrice);

/// <summary>One calendar day of a statement. <c>Planned</c>: in the template and not in a gap.</summary>
public sealed record StatementDay(LocalDate Date, bool Planned, bool Present, LocalTime? Arrival, LocalTime? Departure, bool Meal, AbsenceReason? Absence);

/// <summary>Childcare decision 30: the declaration's fields entered by hand, each over the month's own when given.</summary>
public sealed record DeclaredFigures(
    decimal? NetPay = null, decimal? Upkeep = null, decimal? Meals = null, int? NormalHours = null, int? ActivityDays = null, int? PaidLeaveDays = null,
    decimal? ComplementaryHours = null, decimal? OvertimeHours = null)
{
    public bool AnyNegative => NetPay < 0 || Upkeep < 0 || Meals < 0 || NormalHours < 0 || ActivityDays < 0 || PaidLeaveDays < 0 || ComplementaryHours < 0 || OvertimeHours < 0;
}

/// <summary>Spec §3: the Pajemploi monthly declaration, field by field.</summary>
public sealed record PajemploiDeclaration(
    int NormalHours, int ActivityDays, int? PaidLeaveDays, decimal ComplementaryHours, decimal OvertimeHours,
    decimal NetPay, decimal Upkeep, decimal Meals, decimal Mileage);

/// <summary>Spec §3 and §6: the declaration with the pay's parts, the month's cost and the childcare tax base.</summary>
public sealed record MonthFigures(
    PajemploiDeclaration Declaration, decimal BasePay, decimal Deduction, decimal ExtraPay, decimal PaidLeavePay,
    decimal Cmg, decimal Cost, decimal TaxBase, int ChildSickDeducted);

/// <summary>The snake_case names of <see cref="AbsenceReason"/>, on the wire and in the database.</summary>
public static class AbsenceCodes
{
    private static readonly Dictionary<AbsenceReason, string> Names = new()
    {
        [AbsenceReason.Family] = "family", [AbsenceReason.ParentRtt] = "parent_rtt", [AbsenceReason.ChildSick] = "child_sick",
        [AbsenceReason.CarerAbsent] = "carer_absent", [AbsenceReason.Holiday] = "holiday",
    };

    public static string Of(AbsenceReason reason) => Names[reason];

    public static AbsenceReason? TryParse(string? name) => Names.FirstOrDefault(n => n.Value == name) is { Value: not null } hit ? hit.Key : null;

    public static AbsenceReason Parse(string name) => TryParse(name) ?? throw new ArgumentException($"Unknown absence reason {name}.", nameof(name));
}
