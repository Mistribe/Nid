using Nid.Core.Contracts;
using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.RealEstate.Contracts;

/// <summary>Passive income spec C7: one month of a let property, in its currency.</summary>
public sealed record PropertyIncomeMonth(LocalDate Month, decimal Rent, decimal Costs);

/// <summary>A visible property (archived included) with a tenancy overlapping the range.</summary>
public sealed record PropertyIncomeSource(
    ItemId Item, string Name, Currency Currency, bool Archived,
    IReadOnlyList<PropertyIncomeMonth> Months,          // one per month of the range, first day of the month, oldest first
    IReadOnlyDictionary<LocalDate, decimal?> ValueAt,   // latest valuation on or before each requested date, null if none
    bool LetToday);                                     // a tenancy covers today

/// <summary>RealEstate's net rent by month, read by Investments for its passive income (passive income spec C7, §3.3).</summary>
public interface IPropertyIncome
{
    /// <summary>
    /// Visible properties (archived included) with a tenancy overlapping <paramref name="range"/>. The range must start on the
    /// first day of a month: its first month would otherwise be partial.
    /// </summary>
    Task<Result<IReadOnlyList<PropertyIncomeSource>>> IncomeAsync(
        PersonId reader, VisibilityScope scope, DateRange range, IReadOnlyList<LocalDate> valueDates, LocalDate today, CancellationToken ct);
}
