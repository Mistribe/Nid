using NodaTime;
using UnambitiousFx.Functional;

namespace Nid.Core.Contracts;

/// <summary>Income dashboard spec §5: one rent due, rent only (no charges), prorated, dated on the first covered day.</summary>
public sealed record RentDue(LocalDate On, decimal Amount);

/// <summary>A visible property with at least one tenancy; archived ones included so past months stay.</summary>
public sealed record RentSource(
    ItemId Item, string Name, Currency Currency, string Visibility, IReadOnlyList<OwnerShare> Owners, bool Archived,
    IReadOnlyList<RentDue> Dues,                         // one per month of the range the tenancies cover
    IReadOnlyDictionary<LocalDate, decimal> MonthlyAt,   // PropertyMath.MonthlyRentAt for each date of `at`
    RentDue? Next,                                       // first due strictly after `today` (any month)
    LocalDate? EndsOn);                                  // end of the tenancy covering today when no later tenancy follows

/// <summary>RealEstate's rents for the Income module's household overview (income dashboard spec §5).</summary>
public interface IRentIncome
{
    /// <summary>An open <paramref name="range"/> is read up to the month of <paramref name="today"/>.</summary>
    Task<Result<IReadOnlyList<RentSource>>> RentsAsync(
        PersonId reader, VisibilityScope scope, DateRange range, IReadOnlyList<LocalDate> at, LocalDate today, CancellationToken ct);
}
