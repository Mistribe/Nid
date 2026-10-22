using UnambitiousFx.Functional;

namespace Nid.Core.Contracts;

/// <summary><paramref name="AdultCount"/> counts active and pending adults: both count against the cap.</summary>
public sealed record HouseholdSettingsInfo(int MaxAdults, Currency ReferenceCurrency, int AdultCount);

/// <summary>A field left <c>null</c> is left unchanged.</summary>
public sealed record HouseholdSettingsChange(int? MaxAdults, string? ReferenceCurrency);

public interface IHouseholdSettings
{
    /// <summary>The currency of household totals; EUR while the settings row does not exist yet (it is created at the first login).</summary>
    Task<Currency> ReferenceCurrencyAsync(CancellationToken ct);

    Task<HouseholdSettingsInfo> ReadAsync(CancellationToken ct);

    /// <summary>
    /// The cap is at least 1 and at least the number of adults (<see cref="PersonCodes.CapBelowCount"/>); the currency is a
    /// three-letter upper-case code (<see cref="PersonCodes.UnknownCurrency"/>).
    /// </summary>
    Task<Result<HouseholdSettingsInfo>> UpdateAsync(HouseholdSettingsChange change, CancellationToken ct);

    /// <summary>Issue #104: the household's target share of each asset class, in % (empty: no target).</summary>
    Task<IReadOnlyDictionary<string, decimal>> AllocationTargetAsync(CancellationToken ct);

    /// <summary>Issue #104: replaces the target; checked by the caller (<c>AllocationTarget.Check</c>).</summary>
    Task SaveAllocationTargetAsync(IReadOnlyDictionary<string, decimal> target, CancellationToken ct);
}
